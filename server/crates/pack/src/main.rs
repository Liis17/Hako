use std::{error::Error, io, io::ErrorKind, process, sync::Arc, time::Duration};

use hako_pack::{
    router, AppState, Config, HttpTokenChecker, ObjectStore, PackService, S3ObjectStore,
    UnavailableObjectStore,
};
use sqlx::{postgres::PgPoolOptions, PgPool};
use tokio::net::TcpListener;
use tracing_subscriber::EnvFilter;

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

    let store: Arc<dyn ObjectStore> = match config.s3.as_ref() {
        Some(s3) => Arc::new(S3ObjectStore::new(s3).await),
        None => {
            tracing::warn!("S3 is not configured; pack storage operations will return 503");
            Arc::new(UnavailableObjectStore)
        }
    };
    let token_checker = Arc::new(
        HttpTokenChecker::new(config.auth_internal_url, config.service_secret)
            .map_err(|_| io::Error::other("failed to initialize Auth HTTP client"))?,
    );
    let service = PackService::new(pool, store, config.cleanup_delay);
    let cleanup_service = service.clone();
    tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_secs(30));
        loop {
            interval.tick().await;
            if let Err(error) = cleanup_service.cleanup_once().await {
                tracing::error!(error = %error, "pack cleanup iteration failed");
            }
        }
    });

    let listener = TcpListener::bind(&config.addr).await?;
    tracing::info!(address = %config.addr, "pack listener ready");
    axum::serve(
        listener,
        router(AppState::new(service, token_checker, config.limits)),
    )
    .with_graceful_shutdown(shutdown_signal())
    .await?;
    Ok(())
}

async fn connect_database(database_url: &str) -> Result<PgPool, io::Error> {
    PgPoolOptions::new()
        .max_connections(10)
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
