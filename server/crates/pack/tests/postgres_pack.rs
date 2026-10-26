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
        database_url.clone(),
        Arc::new(store.clone()),
        Arc::new(checker),
        Duration::ZERO,
    );

    let failed_publication_id = Uuid::new_v4();
    sqlx::query("DROP TRIGGER IF EXISTS hako_test_fail_pack_publish ON packs")
        .execute(&pool)
        .await
        .expect("remove prior publication test trigger");
    sqlx::query("DROP FUNCTION IF EXISTS hako_test_fail_pack_publish()")
        .execute(&pool)
        .await
        .expect("remove prior publication test function");
    sqlx::query(
        "CREATE FUNCTION hako_test_fail_pack_publish() RETURNS trigger LANGUAGE plpgsql AS $$ \
         BEGIN IF NEW.name IN ('Failure after object upload', 'Failure after replacement upload') THEN \
         RAISE EXCEPTION 'test publication failure'; END IF; RETURN NEW; END $$",
    )
    .execute(&pool)
    .await
    .expect("install publication failure function");
    sqlx::query(
        "CREATE TRIGGER hako_test_fail_pack_publish BEFORE INSERT OR UPDATE ON packs \
         FOR EACH ROW EXECUTE FUNCTION hako_test_fail_pack_publish()",
    )
    .execute(&pool)
    .await
    .expect("install publication failure trigger");
    let delayed_cleanup_app = test_app(
        pool.clone(),
        database_url.clone(),
        Arc::new(store.clone()),
        Arc::new(mock_checker(owner_one, owner_two)),
        Duration::from_secs(2),
    );
    let publication_failed = request(
        &delayed_cleanup_app,
        "POST",
        &format!("/v1/packs/{failed_publication_id}"),
        Some("owner-one"),
        &[("content-type", "application/zip"), ("if-none-match", "*")],
        make_archive("Failure after object upload"),
    )
    .await;
    assert_eq!(publication_failed.status, StatusCode::INTERNAL_SERVER_ERROR);
    let pending_cleanup_key =
        sqlx::query_scalar::<_, String>("SELECT s3_key FROM pack_cleanup WHERE s3_key LIKE $1")
            .bind(format!("packs/{owner_one}/{failed_publication_id}/%"))
            .fetch_one(&pool)
            .await
            .expect("durable upload cleanup intent");
    assert!(store.contains(&pending_cleanup_key));
    sqlx::query(
        "UPDATE pack_cleanup SET eligible_at = now() + interval '2 seconds' WHERE s3_key = $1",
    )
    .bind(&pending_cleanup_key)
    .execute(&pool)
    .await
    .expect("keep the failed-upload intent in its configured cleanup delay");
    PackService::new(
        pool.clone(),
        database_url.clone(),
        Arc::new(store.clone()),
        Duration::ZERO,
    )
    .cleanup_once()
    .await
    .expect("cleanup should respect the delay after publication failure");
    assert!(store.contains(&pending_cleanup_key));
    tokio::time::sleep(Duration::from_millis(2_100)).await;
    PackService::new(
        pool.clone(),
        database_url.clone(),
        Arc::new(store.clone()),
        Duration::ZERO,
    )
    .cleanup_once()
    .await
    .expect("remove object after failed publication cleanup delay");
    assert!(!store.contains(&pending_cleanup_key));

    // A locked first cleanup candidate must not block later eligible objects.
    const PACK_UPLOAD_GATE_KEY: i64 = -7_000_000_000_000_004;
    let queued_pack_id = Uuid::new_v4();
    let queued_pack = request(
        &app,
        "POST",
        &format!("/v1/packs/{queued_pack_id}"),
        Some("owner-one"),
        &[("content-type", "application/zip"), ("if-none-match", "*")],
        make_archive("Queued before active upload"),
    )
    .await;
    assert_eq!(queued_pack.status, StatusCode::CREATED);
    let queued_etag = queued_pack
        .headers
        .get("etag")
        .expect("created pack ETag")
        .to_str()
        .expect("valid ETag header")
        .to_owned();
    let queued_pack_key = sqlx::query_scalar::<_, String>(
        "SELECT s3_key FROM packs WHERE owner_id = $1 AND pack_id = $2",
    )
    .bind(owner_one)
    .bind(queued_pack_id)
    .fetch_one(&pool)
    .await
    .expect("queued pack object key");
    let queued_delete = request(
        &app,
        "DELETE",
        &format!("/v1/packs/{queued_pack_id}"),
        Some("owner-one"),
        &[("if-match", &queued_etag)],
        Vec::new(),
    )
    .await;
    assert_eq!(queued_delete.status, StatusCode::NO_CONTENT);
    assert!(store.contains(&queued_pack_key));

    sqlx::query("DROP TRIGGER IF EXISTS hako_test_hold_pack_publish ON packs")
        .execute(&pool)
        .await
        .expect("remove prior pack publication gate");
    sqlx::query("DROP FUNCTION IF EXISTS hako_test_hold_pack_publish()")
        .execute(&pool)
        .await
        .expect("remove prior pack publication gate function");
    sqlx::query(
        "CREATE FUNCTION hako_test_hold_pack_publish() RETURNS trigger LANGUAGE plpgsql AS $$ \
         BEGIN IF NEW.name = 'Active upload cleanup' THEN \
         PERFORM pg_advisory_xact_lock(-7000000000000004); \
         RAISE EXCEPTION 'test upload publication failure'; \
         END IF; RETURN NEW; END $$",
    )
    .execute(&pool)
    .await
    .expect("install pack publication gate function");
    sqlx::query(
        "CREATE TRIGGER hako_test_hold_pack_publish BEFORE INSERT OR UPDATE ON packs \
         FOR EACH ROW EXECUTE FUNCTION hako_test_hold_pack_publish()",
    )
    .execute(&pool)
    .await
    .expect("install pack publication gate");
    let mut upload_gate_connection = pool
        .acquire()
        .await
        .expect("acquire active upload gate connection");
    sqlx::query("SELECT pg_advisory_lock($1)")
        .bind(PACK_UPLOAD_GATE_KEY)
        .execute(&mut *upload_gate_connection)
        .await
        .expect("hold active upload publication gate");
    let active_upload_id = Uuid::new_v4();
    let upload_app = app.clone();
    let upload_task = tokio::spawn(async move {
        request(
            &upload_app,
            "POST",
            &format!("/v1/packs/{active_upload_id}"),
            Some("owner-one"),
            &[("content-type", "application/zip"), ("if-none-match", "*")],
            make_archive("Active upload cleanup"),
        )
        .await
    });
    tokio::time::timeout(Duration::from_secs(5), async {
        loop {
            let blocked = sqlx::query_scalar::<_, bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() \
                 AND pid <> pg_backend_pid() AND query LIKE '%INSERT INTO packs%' \
                 AND wait_event_type = 'Lock' AND wait_event = 'advisory')",
            )
            .fetch_one(&pool)
            .await
            .expect("observe upload before publication");
            if blocked {
                break;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("upload reached publication while holding its object lock");
    let active_upload_key =
        sqlx::query_scalar::<_, String>("SELECT s3_key FROM pack_cleanup WHERE s3_key LIKE $1")
            .bind(format!("packs/{owner_one}/{active_upload_id}/%"))
            .fetch_one(&pool)
            .await
            .expect("active upload cleanup intent");
    assert!(store.contains(&active_upload_key));
    sqlx::query(
        "UPDATE pack_cleanup SET eligible_at = now() - interval '10 seconds' WHERE s3_key = $1",
    )
    .bind(&active_upload_key)
    .execute(&pool)
    .await
    .expect("make active upload intent the first eligible cleanup item");
    let cleanup_pool = pool.clone();
    let cleanup_database_url = database_url.clone();
    let cleanup_store = store.clone();
    let mut cleanup_task = tokio::spawn(async move {
        PackService::new(
            cleanup_pool,
            cleanup_database_url,
            Arc::new(cleanup_store),
            Duration::ZERO,
        )
        .cleanup_once()
        .await
    });
    let cleanup_result = tokio::time::timeout(Duration::from_secs(5), &mut cleanup_task).await;
    if cleanup_result.is_err() {
        cleanup_task.abort();
        let _ = cleanup_task.await;
        sqlx::query("SELECT pg_advisory_unlock($1)")
            .bind(PACK_UPLOAD_GATE_KEY)
            .execute(&mut *upload_gate_connection)
            .await
            .expect("release active upload publication gate after cleanup timeout");
        let _ = upload_task.await;
        panic!("cleanup must skip the locked first item and continue to the next item");
    }
    cleanup_result
        .expect("cleanup returns before active upload finishes")
        .expect("cleanup task joins")
        .expect("cleanup pass completes");
    assert!(store.contains(&active_upload_key));
    assert!(!store.contains(&queued_pack_key));
    assert!(
        !sqlx::query_scalar::<_, bool>(
            "SELECT EXISTS (SELECT 1 FROM pack_cleanup WHERE s3_key = $1)"
        )
        .bind(&queued_pack_key)
        .fetch_one(&pool)
        .await
        .expect("queued object cleanup state"),
        "the later eligible object should be cleaned while the first lock is held"
    );
    sqlx::query("SELECT pg_advisory_unlock($1)")
        .bind(PACK_UPLOAD_GATE_KEY)
        .execute(&mut *upload_gate_connection)
        .await
        .expect("release active upload publication gate");
    drop(upload_gate_connection);
    assert_eq!(
        upload_task.await.expect("join active upload").status,
        StatusCode::INTERNAL_SERVER_ERROR
    );
    PackService::new(
        pool.clone(),
        database_url.clone(),
        Arc::new(store.clone()),
        Duration::ZERO,
    )
    .cleanup_once()
    .await
    .expect("clean the formerly locked upload intent after its session ends");
    assert!(!store.contains(&active_upload_key));
    sqlx::query("DROP TRIGGER hako_test_hold_pack_publish ON packs")
        .execute(&pool)
        .await
        .expect("remove active upload publication gate");
    sqlx::query("DROP FUNCTION hako_test_hold_pack_publish()")
        .execute(&pool)
        .await
        .expect("remove active upload publication gate function");

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

    let failed_replacement = request(
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
        make_archive("Failure after replacement upload"),
    )
    .await;
    assert_eq!(failed_replacement.status, StatusCode::INTERNAL_SERVER_ERROR);
    let replacement_cleanup_key =
        sqlx::query_scalar::<_, String>("SELECT s3_key FROM pack_cleanup WHERE s3_key LIKE $1")
            .bind(format!("packs/{owner_one}/{pack_id}/r2-%"))
            .fetch_one(&pool)
            .await
            .expect("durable replacement cleanup intent");
    assert!(store.contains(&replacement_cleanup_key));
    PackService::new(
        pool.clone(),
        database_url.clone(),
        Arc::new(store.clone()),
        Duration::ZERO,
    )
    .cleanup_once()
    .await
    .expect("remove object from unpublished replacement intent");
    assert!(!store.contains(&replacement_cleanup_key));
    let current_after_failed_replacement = request(
        &app,
        "GET",
        &format!("/v1/packs/{pack_id}"),
        Some("owner-one"),
        &[],
        Vec::new(),
    )
    .await;
    assert_eq!(current_after_failed_replacement.status, StatusCode::OK);
    assert_eq!(
        current_after_failed_replacement.metadata()["etag"],
        initial["etag"]
    );
    sqlx::query("DROP TRIGGER hako_test_fail_pack_publish ON packs")
        .execute(&pool)
        .await
        .expect("remove publication failure trigger");
    sqlx::query("DROP FUNCTION hako_test_fail_pack_publish()")
        .execute(&pool)
        .await
        .expect("remove publication failure function");

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

    let restarted_service = PackService::new(
        pool.clone(),
        database_url.clone(),
        Arc::new(store.clone()),
        Duration::ZERO,
    );
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
        database_url.clone(),
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
    database_url: String,
    store: Arc<dyn ObjectStore>,
    checker: Arc<dyn TokenChecker>,
    cleanup_delay: Duration,
) -> Router {
    router(AppState::new(
        PackService::new(pool, database_url, store, cleanup_delay),
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
