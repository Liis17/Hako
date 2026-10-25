use std::{
    env,
    error::Error,
    io::{self, ErrorKind},
    process,
};

use axum::{routing::get, Router};
use sqlx::{postgres::PgPoolOptions, PgPool};
use tokio::net::TcpListener;
use tracing_subscriber::EnvFilter;

struct Config {
    addr: String,
    database_url: String,
}

impl Config {
    fn from_env() -> Result<Self, io::Error> {
        let addr = value_or_default("HAKO_PACK_ADDR", "0.0.0.0:8082")?;
        let database_url = required("HAKO_PACK_DATABASE_URL")?;
        let _auth_internal_url =
            value_or_default("HAKO_PACK_AUTH_INTERNAL_URL", "http://auth:8091")?;

        required("HAKO_PACK_SERVICE_SECRET")?;
        required("HAKO_S3_ENDPOINT")?;
        required("HAKO_S3_BUCKET")?;
        required("HAKO_S3_REGION")?;
        required("HAKO_S3_ACCESS_KEY_ID")?;
        required("HAKO_S3_SECRET_ACCESS_KEY")?;
        let _session_token = optional("HAKO_S3_SESSION_TOKEN")?;
        let path_style = required("HAKO_S3_PATH_STYLE")?;
        path_style.parse::<bool>().map_err(|_| {
            io::Error::new(
                ErrorKind::InvalidInput,
                "environment variable HAKO_S3_PATH_STYLE must be true or false",
            )
        })?;

        Ok(Self { addr, database_url })
    }
}

#[tokio::main]
async fn main() {
    init_tracing();

    if let Err(error) = run().await {
        tracing::error!(error = %error, "pack service stopped");
        process::exit(1);
    }
}

async fn run() -> Result<(), Box<dyn Error + Send + Sync>> {
    let config = Config::from_env()?;
    let pool = connect_database(&config.database_url).await?;
    apply_migrations(&pool).await?;

    let listener = TcpListener::bind(&config.addr).await?;
    tracing::info!(address = %config.addr, "pack listener ready");

    axum::serve(listener, router())
        .with_graceful_shutdown(shutdown_signal())
        .await?;
    Ok(())
}

fn router() -> Router {
    Router::new().route("/healthz", get(healthz))
}

async fn healthz() -> &'static str {
    "ok"
}

async fn connect_database(database_url: &str) -> Result<PgPool, io::Error> {
    PgPoolOptions::new()
        .connect(database_url)
        .await
        .map_err(|_| {
            io::Error::new(
                ErrorKind::ConnectionRefused,
                "failed to connect to pack database",
            )
        })
}

async fn apply_migrations(pool: &PgPool) -> Result<(), io::Error> {
    sqlx::migrate!("./migrations")
        .run(pool)
        .await
        .map_err(|_| io::Error::other("failed to apply pack database migrations"))
}

async fn shutdown_signal() {
    let ctrl_c = async {
        if let Err(error) = tokio::signal::ctrl_c().await {
            tracing::error!(%error, "failed to listen for Ctrl+C");
        }
    };

    #[cfg(unix)]
    let terminate = async {
        match tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()) {
            Ok(mut signal) => {
                signal.recv().await;
            }
            Err(error) => {
                tracing::error!(%error, "failed to listen for SIGTERM");
                std::future::pending::<()>().await;
            }
        }
    };

    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();

    tokio::select! {
        _ = ctrl_c => {},
        _ = terminate => {},
    }
    tracing::info!("shutdown signal received");
}

fn init_tracing() {
    let filter = match EnvFilter::try_from_default_env() {
        Ok(filter) => filter,
        Err(_) => EnvFilter::new("info"),
    };

    if let Err(error) = tracing_subscriber::fmt().with_env_filter(filter).try_init() {
        eprintln!("failed to initialize tracing subscriber: {error}");
        process::exit(1);
    }
}

fn required(name: &'static str) -> Result<String, io::Error> {
    let value = env::var(name).map_err(|error| match error {
        env::VarError::NotPresent => io::Error::new(
            ErrorKind::InvalidInput,
            format!("required environment variable {name} is not set"),
        ),
        env::VarError::NotUnicode(_) => io::Error::new(
            ErrorKind::InvalidInput,
            format!("environment variable {name} must be valid Unicode"),
        ),
    })?;

    if value.trim().is_empty() {
        return Err(io::Error::new(
            ErrorKind::InvalidInput,
            format!("required environment variable {name} must not be empty"),
        ));
    }

    Ok(value)
}

fn optional(name: &'static str) -> Result<Option<String>, io::Error> {
    match env::var(name) {
        Ok(value) if value.is_empty() => Ok(None),
        Ok(value) => Ok(Some(value)),
        Err(env::VarError::NotPresent) => Ok(None),
        Err(env::VarError::NotUnicode(_)) => Err(io::Error::new(
            ErrorKind::InvalidInput,
            format!("environment variable {name} must be valid Unicode"),
        )),
    }
}

fn value_or_default(name: &'static str, default: &str) -> Result<String, io::Error> {
    match env::var(name) {
        Ok(value) if !value.trim().is_empty() => Ok(value),
        Ok(_) => Err(io::Error::new(
            ErrorKind::InvalidInput,
            format!("environment variable {name} must not be empty"),
        )),
        Err(env::VarError::NotPresent) => Ok(default.to_owned()),
        Err(env::VarError::NotUnicode(_)) => Err(io::Error::new(
            ErrorKind::InvalidInput,
            format!("environment variable {name} must be valid Unicode"),
        )),
    }
}
