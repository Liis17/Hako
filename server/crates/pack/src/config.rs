use std::{env, io, io::ErrorKind, time::Duration};

use crate::archive::{PackLimits, MAX_DOWNLOAD_DURATION};

const DEFAULT_CLEANUP_DELAY: Duration = Duration::from_secs(24 * 60 * 60);

#[derive(Clone)]
pub struct Config {
    pub addr: String,
    pub database_url: String,
    pub auth_internal_url: String,
    pub service_secret: String,
    pub s3: Option<S3Config>,
    pub limits: PackLimits,
    pub cleanup_delay: Duration,
}

#[derive(Clone)]
pub struct S3Config {
    pub endpoint: String,
    pub bucket: String,
    pub region: String,
    pub access_key_id: String,
    pub secret_access_key: String,
    pub session_token: Option<String>,
    pub path_style: bool,
}

impl Config {
    pub fn from_env() -> Result<Self, io::Error> {
        let addr = value_or_default("HAKO_PACK_ADDR", "0.0.0.0:8082")?;
        let database_url = required("HAKO_PACK_DATABASE_URL")?;
        let auth_internal_url =
            value_or_default("HAKO_PACK_AUTH_INTERNAL_URL", "http://auth:8091")?;
        let service_secret = required("HAKO_PACK_SERVICE_SECRET")?;
        let limits = PackLimits::from_env()?;
        let cleanup_delay = match env::var("HAKO_PACK_CLEANUP_DELAY") {
            Ok(value) => Duration::from_secs(parse_positive("HAKO_PACK_CLEANUP_DELAY", &value)?),
            Err(env::VarError::NotPresent) => DEFAULT_CLEANUP_DELAY,
            Err(env::VarError::NotUnicode(_)) => {
                return Err(invalid_env("HAKO_PACK_CLEANUP_DELAY must be valid Unicode"))
            }
        };
        if cleanup_delay <= MAX_DOWNLOAD_DURATION || cleanup_delay > DEFAULT_CLEANUP_DELAY {
            return Err(invalid_env(
                "HAKO_PACK_CLEANUP_DELAY must be greater than 3600 and at most 86400 seconds",
            ));
        }

        let endpoint = optional("HAKO_S3_ENDPOINT")?;
        let bucket = optional("HAKO_S3_BUCKET")?;
        let region = optional("HAKO_S3_REGION")?;
        let access_key_id = optional("HAKO_S3_ACCESS_KEY_ID")?;
        let secret_access_key = optional("HAKO_S3_SECRET_ACCESS_KEY")?;
        let session_token = optional("HAKO_S3_SESSION_TOKEN")?;
        let path_style = match env::var("HAKO_S3_PATH_STYLE") {
            Ok(value) if value.trim().is_empty() => true,
            Ok(value) => value
                .parse::<bool>()
                .map_err(|_| invalid_env("HAKO_S3_PATH_STYLE must be true or false"))?,
            Err(env::VarError::NotPresent) => true,
            Err(env::VarError::NotUnicode(_)) => {
                return Err(invalid_env("HAKO_S3_PATH_STYLE must be valid Unicode"))
            }
        };
        let configured = [
            endpoint.as_ref(),
            bucket.as_ref(),
            region.as_ref(),
            access_key_id.as_ref(),
            secret_access_key.as_ref(),
        ];
        let s3 = if configured.iter().all(Option::is_none) {
            None
        } else {
            Some(S3Config {
                endpoint: endpoint.ok_or_else(|| invalid_env("HAKO_S3_ENDPOINT is required"))?,
                bucket: bucket.ok_or_else(|| invalid_env("HAKO_S3_BUCKET is required"))?,
                region: region.ok_or_else(|| invalid_env("HAKO_S3_REGION is required"))?,
                access_key_id: access_key_id
                    .ok_or_else(|| invalid_env("HAKO_S3_ACCESS_KEY_ID is required"))?,
                secret_access_key: secret_access_key
                    .ok_or_else(|| invalid_env("HAKO_S3_SECRET_ACCESS_KEY is required"))?,
                session_token,
                path_style,
            })
        };

        Ok(Self {
            addr,
            database_url,
            auth_internal_url,
            service_secret,
            s3,
            limits,
            cleanup_delay,
        })
    }
}

fn required(name: &'static str) -> Result<String, io::Error> {
    optional(name)?
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| invalid_env(&format!("required environment variable {name} is not set")))
}

fn optional(name: &'static str) -> Result<Option<String>, io::Error> {
    match env::var(name) {
        Ok(value) if value.trim().is_empty() => Ok(None),
        Ok(value) => Ok(Some(value)),
        Err(env::VarError::NotPresent) => Ok(None),
        Err(env::VarError::NotUnicode(_)) => Err(invalid_env(&format!(
            "environment variable {name} must be valid Unicode"
        ))),
    }
}

fn value_or_default(name: &'static str, default: &str) -> Result<String, io::Error> {
    match env::var(name) {
        Ok(value) if !value.trim().is_empty() => Ok(value),
        Ok(_) => Err(invalid_env(&format!(
            "environment variable {name} must not be empty"
        ))),
        Err(env::VarError::NotPresent) => Ok(default.to_owned()),
        Err(env::VarError::NotUnicode(_)) => Err(invalid_env(&format!(
            "environment variable {name} must be valid Unicode"
        ))),
    }
}

fn parse_positive(name: &'static str, value: &str) -> Result<u64, io::Error> {
    value
        .parse::<u64>()
        .ok()
        .filter(|parsed| *parsed > 0)
        .ok_or_else(|| invalid_env(&format!("{name} must be a positive integer")))
}

fn invalid_env(message: &str) -> io::Error {
    io::Error::new(ErrorKind::InvalidInput, message.to_owned())
}
