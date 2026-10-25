use std::{io, sync::Arc};

use axum::{
    body::Body,
    extract::{
        rejection::{PathRejection, QueryRejection},
        Path, Query, Request, State,
    },
    http::{header, HeaderMap, HeaderValue, StatusCode},
    response::{IntoResponse, Response},
    routing::get,
    Json, Router,
};
use futures_util::{stream, StreamExt};
use serde::Deserialize;
use uuid::Uuid;

use crate::{
    archive::{receive_archive, PackLimits, MAX_DOWNLOAD_DURATION},
    auth::{AuthCheckError, TokenChecker},
    error::{PackError, PackResult},
    service::{PackMetadata, PackService},
};

#[derive(Clone)]
pub struct AppState {
    service: PackService,
    token_checker: Arc<dyn TokenChecker>,
    limits: PackLimits,
}

impl AppState {
    pub fn new(
        service: PackService,
        token_checker: Arc<dyn TokenChecker>,
        limits: PackLimits,
    ) -> Self {
        Self {
            service,
            token_checker,
            limits,
        }
    }
}

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/healthz", get(healthz))
        .route("/v1/packs", get(list_packs))
        .route(
            "/v1/packs/{pack_id}",
            get(get_metadata).post(create_pack).delete(delete_pack),
        )
        .route(
            "/v1/packs/{pack_id}/archive",
            get(download_archive).put(replace_archive),
        )
        .fallback(route_not_found)
        .method_not_allowed_fallback(method_not_allowed)
        .with_state(state)
}

async fn healthz() -> &'static str {
    "ok"
}

async fn route_not_found() -> PackError {
    PackError::not_found()
}

async fn method_not_allowed() -> PackError {
    PackError::new(
        StatusCode::METHOD_NOT_ALLOWED,
        "method_not_allowed",
        "Method not allowed.",
    )
}

#[derive(Deserialize)]
struct ListQuery {
    limit: Option<usize>,
    cursor: Option<String>,
}

async fn list_packs(
    State(state): State<AppState>,
    headers: HeaderMap,
    query: Result<Query<ListQuery>, QueryRejection>,
) -> PackResult<impl IntoResponse> {
    let identity = authenticate(&state, &headers).await?;
    let Query(query) = query.map_err(|_| PackError::bad_request())?;
    let limit = query.limit.unwrap_or(50);
    if limit == 0 {
        return Err(PackError::bad_request());
    }
    let result = state
        .service
        .list(identity.account_id, limit.min(200), query.cursor.as_deref())
        .await?;
    Ok(Json(result))
}

async fn get_metadata(
    State(state): State<AppState>,
    path: Result<Path<String>, PathRejection>,
    headers: HeaderMap,
) -> PackResult<Response> {
    let identity = authenticate(&state, &headers).await?;
    let Path(pack_id) = path.map_err(|_| PackError::bad_request())?;
    let pack_id = parse_pack_id(&pack_id)?;
    let metadata = state.service.metadata(identity.account_id, pack_id).await?;
    metadata_response(metadata, StatusCode::OK)
}

async fn create_pack(
    State(state): State<AppState>,
    path: Result<Path<String>, PathRejection>,
    request: Request,
) -> PackResult<Response> {
    let (parts, body) = request.into_parts();
    let identity = authenticate(&state, &parts.headers).await?;
    let Path(pack_id) = path.map_err(|_| PackError::bad_request())?;
    let pack_id = parse_pack_id(&pack_id)?;
    parse_if_none_match(&parts.headers)?;
    if state.service.exists(identity.account_id, pack_id).await? {
        return Err(PackError::precondition_failed());
    }
    validate_content_type(&parts.headers)?;
    let archive = receive_archive(body, content_length(&parts.headers)?, &state.limits).await?;
    let metadata = state
        .service
        .create(identity.account_id, pack_id, archive)
        .await?;
    metadata_response(metadata, StatusCode::CREATED)
}

async fn download_archive(
    State(state): State<AppState>,
    path: Result<Path<String>, PathRejection>,
    headers: HeaderMap,
) -> PackResult<Response> {
    let identity = authenticate(&state, &headers).await?;
    let Path(pack_id) = path.map_err(|_| PackError::bad_request())?;
    let pack_id = parse_pack_id(&pack_id)?;
    let deadline = tokio::time::Instant::now() + MAX_DOWNLOAD_DURATION;
    let (metadata, source) = tokio::time::timeout_at(
        deadline,
        state.service.archive(identity.account_id, pack_id),
    )
    .await
    .map_err(|_| PackError::storage_unavailable())??;
    let body_stream = stream::unfold(
        (source, deadline, false),
        |(mut source, deadline, done)| async move {
            if done {
                return None;
            }
            match tokio::time::timeout_at(deadline, source.next()).await {
                Ok(Some(chunk)) => Some((chunk, (source, deadline, false))),
                Ok(None) => None,
                Err(_) => Some((
                    Err(io::Error::new(
                        io::ErrorKind::TimedOut,
                        "archive download exceeded its time limit",
                    )),
                    (source, deadline, true),
                )),
            }
        },
    );
    let etag = strong_etag(&metadata.etag)?;
    let content_length = HeaderValue::from_str(&metadata.size_bytes.to_string())
        .map_err(|_| PackError::internal())?;
    Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, "application/zip")
        .header(header::CONTENT_LENGTH, content_length)
        .header(header::ETAG, etag)
        .body(Body::from_stream(body_stream))
        .map_err(|_| PackError::internal())
}

async fn replace_archive(
    State(state): State<AppState>,
    path: Result<Path<String>, PathRejection>,
    request: Request,
) -> PackResult<Response> {
    let (parts, body) = request.into_parts();
    let identity = authenticate(&state, &parts.headers).await?;
    let Path(pack_id) = path.map_err(|_| PackError::bad_request())?;
    let pack_id = parse_pack_id(&pack_id)?;
    let expected_etag = parse_if_match(&parts.headers)?;
    state
        .service
        .ensure_etag(identity.account_id, pack_id, &expected_etag)
        .await?;
    validate_content_type(&parts.headers)?;
    let archive = receive_archive(body, content_length(&parts.headers)?, &state.limits).await?;
    let metadata = state
        .service
        .replace(identity.account_id, pack_id, &expected_etag, archive)
        .await?;
    metadata_response(metadata, StatusCode::OK)
}

async fn delete_pack(
    State(state): State<AppState>,
    path: Result<Path<String>, PathRejection>,
    headers: HeaderMap,
) -> PackResult<StatusCode> {
    let identity = authenticate(&state, &headers).await?;
    let Path(pack_id) = path.map_err(|_| PackError::bad_request())?;
    let pack_id = parse_pack_id(&pack_id)?;
    let expected_etag = parse_if_match(&headers)?;
    state
        .service
        .delete(identity.account_id, pack_id, &expected_etag)
        .await?;
    Ok(StatusCode::NO_CONTENT)
}

async fn authenticate(
    state: &AppState,
    headers: &HeaderMap,
) -> PackResult<crate::auth::TokenIdentity> {
    let token = headers
        .get(header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.strip_prefix("Bearer "))
        .filter(|value| !value.is_empty())
        .ok_or_else(PackError::unauthorized)?;
    state
        .token_checker
        .check(token)
        .await
        .map_err(|error| match error {
            AuthCheckError::Unauthorized => PackError::unauthorized(),
            AuthCheckError::Unavailable => PackError::auth_unavailable(),
        })
}

fn parse_pack_id(value: &str) -> PackResult<Uuid> {
    Uuid::parse_str(value).map_err(|_| PackError::bad_request())
}

fn parse_if_none_match(headers: &HeaderMap) -> PackResult<()> {
    let value = headers
        .get(header::IF_NONE_MATCH)
        .ok_or_else(PackError::precondition_required)?
        .to_str()
        .map_err(|_| PackError::bad_request())?;
    if value.trim() == "*" {
        Ok(())
    } else {
        Err(PackError::precondition_failed())
    }
}

fn parse_if_match(headers: &HeaderMap) -> PackResult<String> {
    let value = headers
        .get(header::IF_MATCH)
        .ok_or_else(PackError::precondition_required)?
        .to_str()
        .map_err(|_| PackError::bad_request())?
        .trim();
    let etag = value
        .strip_prefix('"')
        .and_then(|value| value.strip_suffix('"'))
        .filter(|value| !value.is_empty() && !value.contains(['"', ',']))
        .ok_or_else(PackError::precondition_failed)?;
    Ok(etag.to_owned())
}

fn validate_content_type(headers: &HeaderMap) -> PackResult<()> {
    let value = headers
        .get(header::CONTENT_TYPE)
        .and_then(|value| value.to_str().ok())
        .ok_or_else(PackError::unsupported_media_type)?;
    let media_type = value
        .split(';')
        .next()
        .unwrap_or_default()
        .trim()
        .to_ascii_lowercase();
    if media_type == "application/zip" || media_type == "application/octet-stream" {
        Ok(())
    } else {
        Err(PackError::unsupported_media_type())
    }
}

fn content_length(headers: &HeaderMap) -> PackResult<Option<u64>> {
    headers
        .get(header::CONTENT_LENGTH)
        .map(|value| {
            value
                .to_str()
                .ok()
                .and_then(|value| value.parse::<u64>().ok())
                .ok_or_else(PackError::bad_request)
        })
        .transpose()
}

fn strong_etag(etag: &str) -> PackResult<HeaderValue> {
    HeaderValue::from_str(&format!("\"{etag}\"")).map_err(|_| PackError::internal())
}

fn metadata_response(metadata: PackMetadata, status: StatusCode) -> PackResult<Response> {
    let etag = strong_etag(&metadata.etag)?;
    let response = (status, [(header::ETAG, etag)], Json(metadata)).into_response();
    Ok(response)
}

#[cfg(test)]
mod tests {
    use axum::{
        http::{header, HeaderMap, HeaderValue, StatusCode},
        response::IntoResponse,
    };

    use super::{parse_if_match, parse_if_none_match};

    fn status(error: crate::PackError) -> StatusCode {
        error.into_response().status()
    }

    #[test]
    fn if_match_requires_one_strong_quoted_etag() {
        let mut headers = HeaderMap::new();
        assert_eq!(
            status(parse_if_match(&headers).unwrap_err()),
            StatusCode::PRECONDITION_REQUIRED
        );

        headers.insert(header::IF_MATCH, HeaderValue::from_static("\"etag-1\""));
        assert_eq!(parse_if_match(&headers).expect("valid ETag"), "etag-1");

        for value in ["etag-1", "W/\"etag-1\"", "\"etag-1\", \"etag-2\""] {
            headers.insert(header::IF_MATCH, HeaderValue::from_str(value).unwrap());
            assert_eq!(
                status(parse_if_match(&headers).unwrap_err()),
                StatusCode::PRECONDITION_FAILED,
                "invalid If-Match value {value}"
            );
        }
    }

    #[test]
    fn if_none_match_requires_star_for_creation() {
        let mut headers = HeaderMap::new();
        assert_eq!(
            status(parse_if_none_match(&headers).unwrap_err()),
            StatusCode::PRECONDITION_REQUIRED
        );
        headers.insert(header::IF_NONE_MATCH, HeaderValue::from_static("*"));
        assert!(parse_if_none_match(&headers).is_ok());
        headers.insert(
            header::IF_NONE_MATCH,
            HeaderValue::from_static("\"etag-1\""),
        );
        assert_eq!(
            status(parse_if_none_match(&headers).unwrap_err()),
            StatusCode::PRECONDITION_FAILED
        );
    }
}
