use std::{
    net::{IpAddr, SocketAddr},
    sync::{Arc, Mutex},
};

use axum::{
    extract::{ConnectInfo, DefaultBodyLimit, Extension, Path, State},
    http::{HeaderMap, StatusCode},
    response::IntoResponse,
    routing::{get, post},
    Json, Router,
};
use ipnet::IpNet;
use serde::Deserialize;
use sqlx::PgPool;
use uuid::Uuid;

use crate::{
    crypto::{validate_device_name, validate_username, verify_secret},
    error::{AuthError, AuthResult},
    rate_limit::{RateAction, RateLimiter},
    service::AuthService,
};

#[derive(Clone)]
pub struct AppState {
    pub auth: AuthService,
    pub bot_username: Arc<tokio::sync::RwLock<Option<String>>>,
    pub service_secret: Arc<str>,
    pub trusted_proxies: Arc<Vec<IpNet>>,
    pub rate_limiter: Arc<Mutex<RateLimiter>>,
}

impl AppState {
    pub fn new(
        pool: PgPool,
        service_secret: String,
        trusted_proxies: Vec<IpNet>,
        bot_username: Arc<tokio::sync::RwLock<Option<String>>>,
    ) -> Self {
        Self {
            auth: AuthService::system(pool),
            bot_username,
            service_secret: Arc::from(service_secret),
            trusted_proxies: Arc::new(trusted_proxies),
            rate_limiter: Arc::new(Mutex::new(RateLimiter::default())),
        }
    }
}

pub fn public_router(state: AppState) -> Router {
    Router::new()
        .route("/healthz", get(healthz))
        .route("/v1/auth/register", post(register))
        .route("/v1/auth/login", post(login))
        .route("/v1/auth/requests/{id}", get(request_status))
        .route("/v1/auth/exchange", post(exchange))
        .route("/v1/auth/me", get(me))
        .route("/v1/auth/logout", post(logout))
        .fallback(route_not_found)
        .method_not_allowed_fallback(method_not_allowed)
        .with_state(state)
        .layer(DefaultBodyLimit::max(16 * 1024))
}

pub fn internal_router(state: AppState) -> Router {
    Router::new()
        .route("/healthz", get(healthz))
        .route("/internal/v1/tokens/check", post(check_token))
        .fallback(route_not_found)
        .method_not_allowed_fallback(method_not_allowed)
        .with_state(state)
        .layer(DefaultBodyLimit::max(16 * 1024))
}

async fn healthz() -> &'static str {
    "ok"
}

async fn route_not_found() -> AuthError {
    AuthError::not_found()
}

async fn method_not_allowed() -> AuthError {
    AuthError::new(
        StatusCode::METHOD_NOT_ALLOWED,
        "method_not_allowed",
        "Method not allowed.",
    )
}

#[derive(Deserialize)]
struct RegisterInput {
    device_name: String,
}

async fn register(
    State(state): State<AppState>,
    headers: HeaderMap,
    peer: Option<Extension<ConnectInfo<SocketAddr>>>,
    body: Result<Json<RegisterInput>, axum::extract::rejection::JsonRejection>,
) -> AuthResult<impl IntoResponse> {
    check_rate(&state, &headers, peer, RateAction::Register)?;
    let Json(input) = body.map_err(AuthError::from_json_rejection)?;
    if !validate_device_name(&input.device_name) {
        return Err(AuthError::bad_request());
    }
    let bot_username = state.bot_username.read().await.clone().ok_or_else(|| {
        AuthError::new(
            StatusCode::SERVICE_UNAVAILABLE,
            "bot_unavailable",
            "Telegram bot is not ready.",
        )
    })?;
    let response = state
        .auth
        .register(&input.device_name, &bot_username)
        .await?;
    Ok((StatusCode::CREATED, Json(response)))
}

#[derive(Deserialize)]
struct LoginInput {
    username: String,
    device_name: String,
}

async fn login(
    State(state): State<AppState>,
    headers: HeaderMap,
    peer: Option<Extension<ConnectInfo<SocketAddr>>>,
    body: Result<Json<LoginInput>, axum::extract::rejection::JsonRejection>,
) -> AuthResult<impl IntoResponse> {
    check_rate(&state, &headers, peer, RateAction::Login)?;
    let Json(input) = body.map_err(AuthError::from_json_rejection)?;
    if !validate_username(&input.username) || !validate_device_name(&input.device_name) {
        return Err(AuthError::bad_request());
    }
    let response = state
        .auth
        .login(&input.username, &input.device_name)
        .await?;
    Ok((StatusCode::CREATED, Json(response)))
}

async fn request_status(
    State(state): State<AppState>,
    Path(id): Path<String>,
    headers: HeaderMap,
) -> AuthResult<Json<crate::service::RequestStatusResponse>> {
    let request_id = parse_id(&id)?;
    let exchange_secret = bearer(&headers)?;
    let response = state
        .auth
        .request_status(request_id, exchange_secret)
        .await?;
    Ok(Json(response))
}

#[derive(Deserialize)]
struct ExchangeInput {
    request_id: String,
}

async fn exchange(
    State(state): State<AppState>,
    headers: HeaderMap,
    peer: Option<Extension<ConnectInfo<SocketAddr>>>,
    body: Result<Json<ExchangeInput>, axum::extract::rejection::JsonRejection>,
) -> AuthResult<Json<crate::service::ExchangeResponse>> {
    check_rate(&state, &headers, peer, RateAction::Exchange)?;
    let Json(input) = body.map_err(AuthError::from_json_rejection)?;
    let request_id = parse_id(&input.request_id)?;
    let exchange_secret = bearer(&headers)?;
    Ok(Json(
        state.auth.exchange(request_id, exchange_secret).await?,
    ))
}

async fn me(
    State(state): State<AppState>,
    headers: HeaderMap,
) -> AuthResult<Json<crate::service::MeResponse>> {
    let token = bearer(&headers)?;
    Ok(Json(state.auth.me(token).await?))
}

async fn logout(State(state): State<AppState>, headers: HeaderMap) -> AuthResult<StatusCode> {
    let token = bearer(&headers)?;
    state.auth.logout(token).await?;
    Ok(StatusCode::NO_CONTENT)
}

#[derive(Deserialize)]
struct CheckTokenInput {
    token: String,
}

async fn check_token(
    State(state): State<AppState>,
    headers: HeaderMap,
    body: Result<Json<CheckTokenInput>, axum::extract::rejection::JsonRejection>,
) -> AuthResult<impl IntoResponse> {
    let provided = headers
        .get("X-Service-Secret")
        .and_then(|value| value.to_str().ok())
        .ok_or_else(AuthError::unauthorized)?;
    if !verify_secret(provided, &state.service_secret) {
        return Err(AuthError::unauthorized());
    }
    let Json(input) = body.map_err(AuthError::from_json_rejection)?;
    if input.token.is_empty() || input.token.len() > 256 {
        return Err(AuthError::bad_request());
    }
    Ok(Json(state.auth.check_token(&input.token).await?))
}

fn parse_id(value: &str) -> AuthResult<Uuid> {
    Uuid::parse_str(value).map_err(|_| AuthError::bad_request())
}

fn bearer(headers: &HeaderMap) -> AuthResult<&str> {
    headers
        .get(axum::http::header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.strip_prefix("Bearer "))
        .filter(|value| !value.is_empty())
        .ok_or_else(AuthError::unauthorized)
}

fn check_rate(
    state: &AppState,
    headers: &HeaderMap,
    peer: Option<Extension<ConnectInfo<SocketAddr>>>,
    action: RateAction,
) -> AuthResult<()> {
    let peer_ip = peer.map(|Extension(ConnectInfo(address))| address.ip());
    let ip = client_ip(headers, peer_ip, &state.trusted_proxies);
    let now = state.auth.clock_now().unix_timestamp();
    let Ok(mut limiter) = state.rate_limiter.lock() else {
        return Err(AuthError::internal());
    };
    if !limiter.allow(ip, action, now) {
        return Err(AuthError::new(
            StatusCode::TOO_MANY_REQUESTS,
            "rate_limited",
            "Too many requests. Try again later.",
        ));
    }
    Ok(())
}

fn client_ip(headers: &HeaderMap, peer: Option<IpAddr>, trusted: &[IpNet]) -> IpAddr {
    let Some(peer) = peer else {
        return IpAddr::from([127, 0, 0, 1]);
    };
    if !is_trusted(peer, trusted) {
        return peer;
    }
    let forwarded = headers
        .get("X-Forwarded-For")
        .and_then(|value| value.to_str().ok())
        .into_iter()
        .flat_map(|value| value.split(','))
        .filter_map(|value| value.trim().parse::<IpAddr>().ok())
        .collect::<Vec<_>>();
    if forwarded.is_empty() {
        return peer;
    }
    let mut chain = forwarded;
    chain.push(peer);
    while chain.len() > 1 && chain.last().is_some_and(|ip| is_trusted(*ip, trusted)) {
        chain.pop();
    }
    chain.last().copied().unwrap_or(peer)
}

fn is_trusted(ip: IpAddr, trusted: &[IpNet]) -> bool {
    trusted.iter().any(|network| network.contains(&ip))
}

#[cfg(test)]
mod tests {
    use std::net::IpAddr;

    use axum::http::{HeaderMap, HeaderValue};
    use ipnet::IpNet;

    use super::client_ip;

    #[test]
    fn forwarded_addresses_are_used_only_for_trusted_peers() {
        let mut headers = HeaderMap::new();
        headers.insert(
            "X-Forwarded-For",
            HeaderValue::from_static("198.51.100.44, 10.0.0.2"),
        );
        let trusted = vec!["10.0.0.0/8".parse::<IpNet>().expect("CIDR")];
        let proxy: IpAddr = "10.0.0.1".parse().expect("proxy IP");
        let direct: IpAddr = "203.0.113.8".parse().expect("peer IP");

        assert_eq!(
            client_ip(&headers, Some(proxy), &trusted),
            "198.51.100.44".parse::<IpAddr>().expect("client IP")
        );
        assert_eq!(client_ip(&headers, Some(direct), &trusted), direct);
    }
}
