use std::{collections::HashMap, env, io::Write, sync::Arc, time::Duration};

use async_trait::async_trait;
use axum::{
    body::Body,
    http::{Request, StatusCode},
    Router,
};
use hako_pack::{
    router, AppState, AuthCheckError, InMemoryObjectStore, ObjectStore, PackLimits, PackService,
    TokenChecker, TokenIdentity, UnavailableObjectStore,
};
use http_body_util::BodyExt;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use sqlx::{postgres::PgPoolOptions, PgPool};
use tower::ServiceExt;
use uuid::Uuid;
use zip::{write::FileOptions, ZipWriter};

#[derive(Default)]
struct MockTokenChecker {
    tokens: HashMap<String, Result<TokenIdentity, AuthCheckError>>,
}

#[async_trait]
impl TokenChecker for MockTokenChecker {
    async fn check(&self, token: &str) -> Result<TokenIdentity, AuthCheckError> {
        self.tokens
            .get(token)
            .cloned()
            .unwrap_or(Err(AuthCheckError::Unauthorized))
    }
}

struct ApiResponse {
    status: StatusCode,
    headers: axum::http::HeaderMap,
    body: Vec<u8>,
}

impl ApiResponse {
    fn json(&self) -> Value {
        serde_json::from_slice(&self.body).expect("response should contain JSON")
    }

    fn metadata(&self) -> Value {
        self.json()
    }
}

#[tokio::test]
async fn postgres_pack_api_full_lifecycle_and_isolation() {
    let Ok(database_url) = env::var("HAKO_TEST_DATABASE_URL") else {
        println!("SKIP: HAKO_TEST_DATABASE_URL is unset; Pack PostgreSQL integration coverage was not run.");
        return;
    };

    let pool = PgPoolOptions::new()
        .max_connections(10)
        .connect(&database_url)
        .await
        .expect("connect HAKO_TEST_DATABASE_URL");
    sqlx::migrate!("./migrations")
        .run(&pool)
        .await
        .expect("apply Pack migrations to the isolated test database");

    let owner_one = Uuid::new_v4();
    let owner_two = Uuid::new_v4();
    let checker = mock_checker(owner_one, owner_two);
    let store = InMemoryObjectStore::default();
    let app = test_app(
        pool.clone(),
        Arc::new(store.clone()),
        Arc::new(checker),
        Duration::ZERO,
    );
    let pack_id = Uuid::new_v4();
    let initial_archive = make_archive("First revision");

    let missing_condition = request(
        &app,
        "POST",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[("content-type", "application/zip")],
        initial_archive.clone(),
    )
    .await;
    assert_eq!(missing_condition.status, StatusCode::PRECONDITION_REQUIRED);

    let created = request(
        &app,
        "POST",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[("content-type", "application/zip"), ("if-none-match", "*")],
        initial_archive.clone(),
    )
    .await;
    assert_eq!(created.status, StatusCode::CREATED);
    let initial = created.metadata();
    assert_eq!(initial["revision"], 1);
    assert_eq!(initial["sha256"], sha256(&initial_archive));
    assert_eq!(
        created.headers["etag"],
        format!("\"{}\"", initial["etag"].as_str().unwrap())
    );

    let duplicate = request(
        &app,
        "POST",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[("content-type", "application/zip"), ("if-none-match", "*")],
        initial_archive.clone(),
    )
    .await;
    assert_eq!(duplicate.status, StatusCode::PRECONDITION_FAILED);

    let metadata = request(
        &app,
        "GET",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(metadata.status, StatusCode::OK);
    assert_eq!(metadata.metadata()["etag"], initial["etag"]);

    let isolated_owner = request(
        &app,
        "GET",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-two"),
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(isolated_owner.status, StatusCode::NOT_FOUND);

    let same_id_other_owner = request(
        &app,
        "POST",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-two"),
        &[
            ("content-type", "application/octet-stream"),
            ("if-none-match", "*"),
        ],
        initial_archive.clone(),
    )
    .await;
    assert_eq!(same_id_other_owner.status, StatusCode::CREATED);

    let second_id = Uuid::new_v4();
    let second_created = request(
        &app,
        "POST",
        &format!("/v1/packs/{second_id}"),
        Some("owner-one"),
        &[("content-type", "application/zip"), ("if-none-match", "*")],
        make_archive("Second pack"),
    )
    .await;
    assert_eq!(second_created.status, StatusCode::CREATED);

    let page_one = request(
        &app,
        "GET",
        "/v1/packs?limit=1",
        Some("owner-one"),
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(page_one.status, StatusCode::OK);
    let page_one_json = page_one.json();
    let cursor = page_one_json["next_cursor"]
        .as_str()
        .expect("first page should have a next cursor");
    assert_eq!(page_one_json["items"].as_array().unwrap().len(), 1);
    let page_two = request(
        &app,
        "GET",
        &format!("/v1/packs?limit=1&cursor={cursor}"),
        Some("owner-one"),
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(page_two.status, StatusCode::OK);
    assert!(page_two.json()["items"].as_array().unwrap().len() == 1);
    assert_ne!(
        page_one_json["items"][0]["pack_id"],
        page_two.json()["items"][0]["pack_id"]
    );

    let missing_match = request(
        &app,
        "PUT",
        &format!("/v1/packs/{pack_id}/archive"),
        Some("owner-one"),
        &[("content-type", "application/zip")],
        make_archive("Replacement"),
    )
    .await;
    assert_eq!(missing_match.status, StatusCode::PRECONDITION_REQUIRED);

    let stale_match = request(
        &app,
        "PUT",
        &format!("/v1/packs/{pack_id}/archive"),
        Some("owner-one"),
        &[
            ("content-type", "application/zip"),
            ("if-match", "\"stale-etag\""),
        ],
        make_archive("Replacement"),
    )
    .await;
    assert_eq!(stale_match.status, StatusCode::PRECONDITION_FAILED);

    let original_key = key(owner_one, pack_id, 1, initial["etag"].as_str().unwrap());
    store.fail_next_put();
    let failed_store_write = request(
        &app,
        "PUT",
        &format!("/v1/packs/{pack_id}/archive"),
        Some("owner-one"),
        &[
            ("content-type", "application/zip"),
            (
                "if-match",
                &format!("\"{}\"", initial["etag"].as_str().unwrap()),
            ),
        ],
        make_archive("Replacement that fails in storage"),
    )
    .await;
    assert_eq!(failed_store_write.status, StatusCode::SERVICE_UNAVAILABLE);
    assert!(store.contains(&original_key));

    let replacement_archive = make_archive("Second revision");
    let replaced = request(
        &app,
        "PUT",
        &format!("/v1/packs/{pack_id}/archive"),
        Some("owner-one"),
        &[
            ("content-type", "application/zip"),
            (
                "if-match",
                &format!("\"{}\"", initial["etag"].as_str().unwrap()),
            ),
        ],
        replacement_archive.clone(),
    )
    .await;
    assert_eq!(replaced.status, StatusCode::OK);
    let replacement = replaced.metadata();
    assert_eq!(replacement["revision"], 2);
    assert_ne!(replacement["etag"], initial["etag"]);
    assert_ne!(replacement["sha256"], initial["sha256"]);
    assert_eq!(replacement["sha256"], sha256(&replacement_archive));

    let download = request(
        &app,
        "GET",
        &format!("/v1/packs/{pack_id}/archive"),
        Some("owner-one"),
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(download.status, StatusCode::OK);
    assert_eq!(download.headers["content-type"], "application/zip");
    assert_eq!(
        download.headers["content-length"],
        replacement_archive.len().to_string()
    );
    assert_eq!(
        download.headers["etag"],
        format!("\"{}\"", replacement["etag"].as_str().unwrap())
    );
    assert_eq!(download.body, replacement_archive);
    assert_eq!(sha256(&download.body), replacement["sha256"]);

    let concurrent_one = make_archive("Concurrent winner A");
    let concurrent_two = make_archive("Concurrent winner B");
    let match_header = format!("\"{}\"", replacement["etag"].as_str().unwrap());
    let concurrent_path = format!("/v1/packs/{pack_id}/archive");
    let concurrent_headers = [
        ("content-type", "application/zip"),
        ("if-match", match_header.as_str()),
    ];
    let (concurrent_a, concurrent_b) = tokio::join!(
        request(
            &app,
            "PUT",
            &concurrent_path,
            Some("owner-one"),
            &concurrent_headers,
            concurrent_one,
        ),
        request(
            &app,
            "PUT",
            &concurrent_path,
            Some("owner-one"),
            &concurrent_headers,
            concurrent_two,
        )
    );
    let statuses = [concurrent_a.status, concurrent_b.status];
    assert!(statuses.contains(&StatusCode::OK));
    assert!(statuses.contains(&StatusCode::PRECONDITION_FAILED));

    let current = request(
        &app,
        "GET",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[],
        Vec::new(),
    )
    .await
    .metadata();
    let missing_delete_condition = request(
        &app,
        "DELETE",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(
        missing_delete_condition.status,
        StatusCode::PRECONDITION_REQUIRED
    );
    let stale_delete = request(
        &app,
        "DELETE",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[("if-match", "\"stale-etag\"")],
        Vec::new(),
    )
    .await;
    assert_eq!(stale_delete.status, StatusCode::PRECONDITION_FAILED);
    let deleted = request(
        &app,
        "DELETE",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[(
            "if-match",
            &format!("\"{}\"", current["etag"].as_str().unwrap()),
        )],
        Vec::new(),
    )
    .await;
    assert_eq!(deleted.status, StatusCode::NO_CONTENT);
    let not_found = request(
        &app,
        "GET",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(not_found.status, StatusCode::NOT_FOUND);

    let restarted_service = PackService::new(pool.clone(), Arc::new(store.clone()), Duration::ZERO);
    restarted_service
        .cleanup_once()
        .await
        .expect("new service instance should resume cleanup queue");
    assert!(!store.contains(&original_key));

    let unauthorized = request(&app, "GET", "/v1/packs", Some("invalid"), &[], Vec::new()).await;
    assert_eq!(unauthorized.status, StatusCode::UNAUTHORIZED);
    let auth_unavailable =
        request(&app, "GET", "/v1/packs", Some("auth-down"), &[], Vec::new()).await;
    assert_eq!(auth_unavailable.status, StatusCode::SERVICE_UNAVAILABLE);

    let no_store_app = test_app(
        pool.clone(),
        Arc::new(UnavailableObjectStore),
        Arc::new(mock_checker(owner_one, owner_two)),
        Duration::ZERO,
    );
    let health = request(&no_store_app, "GET", "/healthz", None, &[], Vec::new()).await;
    assert_eq!(health.status, StatusCode::OK);
    let unavailable_store = request(
        &no_store_app,
        "POST",
        &format!("/v1/packs/{}", Uuid::new_v4()),
        Some("owner-one"),
        &[("content-type", "application/zip"), ("if-none-match", "*")],
        make_archive("S3 disabled"),
    )
    .await;
    assert_eq!(unavailable_store.status, StatusCode::SERVICE_UNAVAILABLE);

    pool.close().await;
}

fn test_app(
    pool: PgPool,
    store: Arc<dyn ObjectStore>,
    checker: Arc<dyn TokenChecker>,
    cleanup_delay: Duration,
) -> Router {
    router(AppState::new(
        PackService::new(pool, store, cleanup_delay),
        checker,
        PackLimits::default(),
    ))
}

fn mock_checker(owner_one: Uuid, owner_two: Uuid) -> MockTokenChecker {
    let identity = |account_id| TokenIdentity {
        account_id,
        username: "test".to_owned(),
        session_id: Uuid::new_v4(),
        expires_at: "2099-01-01T00:00:00Z".to_owned(),
    };
    MockTokenChecker {
        tokens: HashMap::from([
            ("owner-one".to_owned(), Ok(identity(owner_one))),
            ("owner-two".to_owned(), Ok(identity(owner_two))),
            ("auth-down".to_owned(), Err(AuthCheckError::Unavailable)),
        ]),
    }
}

async fn request(
    app: &Router,
    method: &str,
    uri: &str,
    token: Option<&str>,
    headers: &[(&str, &str)],
    body: Vec<u8>,
) -> ApiResponse {
    let mut builder = Request::builder().method(method).uri(uri);
    if let Some(token) = token {
        builder = builder.header("authorization", format!("Bearer {token}"));
    }
    for (name, value) in headers {
        builder = builder.header(*name, *value);
    }
    let response = app
        .clone()
        .oneshot(builder.body(Body::from(body)).expect("build request"))
        .await
        .expect("invoke Pack router");
    let (parts, body) = response.into_parts();
    let body = body
        .collect()
        .await
        .expect("collect response body")
        .to_bytes()
        .to_vec();
    ApiResponse {
        status: parts.status,
        headers: parts.headers,
        body,
    }
}

fn make_archive(name: &str) -> Vec<u8> {
    let manifest = json!({
        "format_version": 1,
        "name": name,
        "minecraft_version": "1.21.4",
        "loader": "fabric",
        "profile": {"custom_client_setting": {"keep": true}},
        "modrinth_files": []
    });
    let mut writer = ZipWriter::new(std::io::Cursor::new(Vec::new()));
    writer
        .start_file("manifest.json", FileOptions::default())
        .expect("start manifest");
    writer
        .write_all(&serde_json::to_vec(&manifest).expect("serialize manifest"))
        .expect("write manifest");
    writer
        .start_file("overrides/options.txt", FileOptions::default())
        .expect("start override");
    writer.write_all(name.as_bytes()).expect("write override");
    writer.finish().expect("finish archive").into_inner()
}

fn sha256(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    digest.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn key(owner: Uuid, pack: Uuid, revision: i64, etag: &str) -> String {
    format!("packs/{owner}/{pack}/r{revision}-{etag}.zip")
}
