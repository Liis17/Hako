use std::time::Duration;

use async_trait::async_trait;
use serde::Deserialize;
use uuid::Uuid;

#[derive(Debug, Clone, Deserialize)]
pub struct TokenIdentity {
    pub account_id: Uuid,
    pub username: String,
    pub session_id: Uuid,
    pub expires_at: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AuthCheckError {
    Unauthorized,
    Unavailable,
}

#[async_trait]
pub trait TokenChecker: Send + Sync {
    async fn check(&self, token: &str) -> Result<TokenIdentity, AuthCheckError>;
}

pub struct HttpTokenChecker {
    client: reqwest::Client,
    url: String,
    service_secret: String,
}

impl HttpTokenChecker {
    pub fn new(base_url: String, service_secret: String) -> Result<Self, reqwest::Error> {
        Ok(Self {
            client: reqwest::Client::builder()
                .timeout(Duration::from_secs(2))
                .build()?,
            url: format!(
                "{}/internal/v1/tokens/check",
                base_url.trim_end_matches('/')
            ),
            service_secret,
        })
    }
}

#[async_trait]
impl TokenChecker for HttpTokenChecker {
    async fn check(&self, token: &str) -> Result<TokenIdentity, AuthCheckError> {
        let response = self
            .client
            .post(&self.url)
            .header("X-Service-Secret", &self.service_secret)
            .json(&serde_json::json!({ "token": token }))
            .send()
            .await
            .map_err(|_| AuthCheckError::Unavailable)?;

        if response.status() == reqwest::StatusCode::UNAUTHORIZED {
            return Err(AuthCheckError::Unauthorized);
        }
        if !response.status().is_success() {
            return Err(AuthCheckError::Unavailable);
        }

        response
            .json::<TokenIdentity>()
            .await
            .map_err(|_| AuthCheckError::Unavailable)
    }
}
