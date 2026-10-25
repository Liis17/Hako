use std::{
    env,
    error::Error,
    io::{self, ErrorKind},
    process,
    sync::Arc,
};

use hako_auth::{
    api::{internal_router, public_router, AppState},
    bot_worker::run_bot_worker,
    telegram::{BotClient, ReqwestBotClient},
};
use ipnet::IpNet;
use sqlx::{postgres::PgPoolOptions, PgPool};
use tokio::{net::TcpListener, sync::RwLock};
use tracing_subscriber::EnvFilter;

struct Config {
    public_addr: String,
    internal_addr: String,
    database_url: String,
    telegram_bot_token: String,
    service_secret: String,
    trusted_proxies: Vec<IpNet>,
}

impl Config {
    fn from_env() -> Result<Self, io::Error> {
        let public_addr = value_or_default("HAKO_AUTH_PUBLIC_ADDR", "0.0.0.0:8081")?;
        let internal_addr = value_or_default("HAKO_AUTH_INTERNAL_ADDR", "0.0.0.0:8091")?;
        let database_url = required("HAKO_AUTH_DATABASE_URL")?;
        let telegram_bot_token = required("HAKO_TELEGRAM_BOT_TOKEN")?;
        let service_secret = required("HAKO_AUTH_SERVICE_SECRET")?;
        let trusted_proxies = optional("HAKO_AUTH_TRUSTED_PROXIES")?
            .unwrap_or_default()
            .split(',')
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(|value| {
                value.parse::<IpNet>().map_err(|_| {
                    io::Error::new(
                        ErrorKind::InvalidInput,
                        "HAKO_AUTH_TRUSTED_PROXIES must contain comma-separated CIDR values",
                    )
                })
            })
            .collect::<Result<Vec<_>, _>>()?;

        Ok(Self {
            public_addr,
            internal_addr,
            database_url,
            telegram_bot_token,
            service_secret,
            trusted_proxies,
        })
    }
}

#[tokio::main]
async fn main() {
    init_tracing();

    if let Err(error) = run().await {
        tracing::error!(error = %error, "auth service stopped");
        process::exit(1);
    }
}

async fn run() -> Result<(), Box<dyn Error + Send + Sync>> {
    let config = Config::from_env()?;
    let pool = connect_database(&config.database_url).await?;
    apply_migrations(&pool).await?;

    let bot: Arc<dyn BotClient> = Arc::new(
        ReqwestBotClient::new(config.telegram_bot_token)
            .map_err(|_| io::Error::other("failed to initialize Telegram HTTP client"))?,
    );
    let bot_username = Arc::new(RwLock::new(None));
    let state = AppState::new(
        pool.clone(),
        config.service_secret,
        config.trusted_proxies,
        bot_username.clone(),
    );
    let bot_task = tokio::spawn(run_bot_worker(state.auth.clone(), bot, bot_username));

    let public_listener = TcpListener::bind(&config.public_addr).await?;
    let internal_listener = TcpListener::bind(&config.internal_addr).await?;
    tracing::info!(address = %config.public_addr, "auth public listener ready");
    tracing::info!(address = %config.internal_addr, "auth internal listener ready");

    let public_server = axum::serve(
        public_listener,
        public_router(state.clone()).into_make_service_with_connect_info::<std::net::SocketAddr>(),
    )
    .with_graceful_shutdown(shutdown_signal());
    let internal_server = axum::serve(
        internal_listener,
        internal_router(state).into_make_service_with_connect_info::<std::net::SocketAddr>(),
    )
    .with_graceful_shutdown(shutdown_signal());

    tokio::try_join!(public_server, internal_server)?;
    bot_task.abort();
    Ok(())
}

async fn connect_database(database_url: &str) -> Result<PgPool, io::Error> {
    PgPoolOptions::new()
        .connect(database_url)
        .await
        .map_err(|_| {
            io::Error::new(
                ErrorKind::ConnectionRefused,
                "failed to connect to auth database",
            )
        })
}

async fn apply_migrations(pool: &PgPool) -> Result<(), io::Error> {
    sqlx::migrate!("./migrations")
        .run(pool)
        .await
        .map_err(|_| io::Error::other("failed to apply auth database migrations"))
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
