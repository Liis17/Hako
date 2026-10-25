use axum::{
    http::{header, HeaderValue, StatusCode},
    response::{IntoResponse, Response},
    Json,
};
use serde_json::json;
use std::fmt;

#[derive(Debug, Clone, Copy)]
pub struct PackError {
    status: StatusCode,
    code: &'static str,
    message: &'static str,
    retry_after: Option<&'static str>,
}

impl PackError {
    pub const fn new(status: StatusCode, code: &'static str, message: &'static str) -> Self {
        Self {
            status,
            code,
            message,
            retry_after: None,
        }
    }

    pub const fn bad_request() -> Self {
        Self::new(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "Invalid request.",
        )
    }

    pub const fn unauthorized() -> Self {
        Self::new(
            StatusCode::UNAUTHORIZED,
            "unauthorized",
            "Authentication required.",
        )
    }

    pub const fn not_found() -> Self {
        Self::new(StatusCode::NOT_FOUND, "not_found", "Pack not found.")
    }

    pub const fn precondition_required() -> Self {
        Self::new(
            StatusCode::PRECONDITION_REQUIRED,
            "precondition_required",
            "A conditional request header is required.",
        )
    }

    pub const fn precondition_failed() -> Self {
        Self::new(
            StatusCode::PRECONDITION_FAILED,
            "precondition_failed",
            "The request precondition failed.",
        )
    }

    pub const fn payload_too_large() -> Self {
        Self::new(
            StatusCode::PAYLOAD_TOO_LARGE,
            "payload_too_large",
            "Pack archive exceeds the configured size limit.",
        )
    }

    pub const fn invalid_archive() -> Self {
        Self::new(
            StatusCode::BAD_REQUEST,
            "invalid_archive",
            "The uploaded file is not a valid Hako pack archive.",
        )
    }

    pub const fn unsupported_media_type() -> Self {
        Self::new(
            StatusCode::UNSUPPORTED_MEDIA_TYPE,
            "unsupported_media_type",
            "Content-Type must be application/zip or application/octet-stream.",
        )
    }

    pub const fn auth_unavailable() -> Self {
        Self::new(
            StatusCode::SERVICE_UNAVAILABLE,
            "auth_unavailable",
            "Authentication service is unavailable.",
        )
    }

    pub const fn storage_unavailable() -> Self {
        Self::new(
            StatusCode::SERVICE_UNAVAILABLE,
            "storage_unavailable",
            "Pack storage is unavailable.",
        )
    }

    pub const fn validation_busy() -> Self {
        Self {
            status: StatusCode::SERVICE_UNAVAILABLE,
            code: "validation_busy",
            message: "Archive validation capacity is temporarily unavailable.",
            retry_after: Some("1"),
        }
    }

    pub const fn internal() -> Self {
        Self::new(
            StatusCode::INTERNAL_SERVER_ERROR,
            "internal_error",
            "The request could not be completed.",
        )
    }
}

impl fmt::Display for PackError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{} ({})", self.message, self.code)
    }
}

impl std::error::Error for PackError {}

impl IntoResponse for PackError {
    fn into_response(self) -> Response {
        let mut response = (
            self.status,
            Json(json!({
                "error": {
                    "code": self.code,
                    "message": self.message,
                }
            })),
        )
            .into_response();
        if let Some(retry_after) = self.retry_after {
            response
                .headers_mut()
                .insert(header::RETRY_AFTER, HeaderValue::from_static(retry_after));
        }
        response
    }
}

impl From<sqlx::Error> for PackError {
    fn from(_: sqlx::Error) -> Self {
        Self::internal()
    }
}

pub type PackResult<T> = Result<T, PackError>;
