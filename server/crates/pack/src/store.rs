use std::{
    collections::HashMap,
    io,
    path::Path,
    pin::Pin,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
};

use async_trait::async_trait;
use aws_credential_types::Credentials;
use aws_sdk_s3::{primitives::ByteStream, Client};
use bytes::Bytes;
use futures_util::{stream, Stream};
use tokio_util::io::ReaderStream;

use crate::config::S3Config;

pub type ObjectStream = Pin<Box<dyn Stream<Item = Result<Bytes, io::Error>> + Send>>;

#[derive(Debug, Clone, Copy)]
pub struct StoreError;

#[async_trait]
pub trait ObjectStore: Send + Sync {
    async fn put_file(&self, key: &str, path: &Path, size_bytes: u64) -> Result<(), StoreError>;
    async fn get(&self, key: &str) -> Result<ObjectStream, StoreError>;
    async fn delete(&self, key: &str) -> Result<(), StoreError>;
}

pub struct S3ObjectStore {
    client: Client,
    bucket: String,
}

impl S3ObjectStore {
    pub async fn new(config: &S3Config) -> Self {
        let credentials = Credentials::new(
            config.access_key_id.clone(),
            config.secret_access_key.clone(),
            config.session_token.clone(),
            None,
            "hako-pack",
        );
        let shared = aws_config::defaults(aws_config::BehaviorVersion::latest())
            .region(aws_config::Region::new(config.region.clone()))
            .credentials_provider(credentials)
            .load()
            .await;
        let s3_config = aws_sdk_s3::config::Builder::from(&shared)
            .endpoint_url(&config.endpoint)
            .force_path_style(config.path_style)
            .build();

        Self {
            client: Client::from_conf(s3_config),
            bucket: config.bucket.clone(),
        }
    }
}

#[async_trait]
impl ObjectStore for S3ObjectStore {
    async fn put_file(&self, key: &str, path: &Path, size_bytes: u64) -> Result<(), StoreError> {
        let body = ByteStream::from_path(path).await.map_err(|_| StoreError)?;
        self.client
            .put_object()
            .bucket(&self.bucket)
            .key(key)
            .content_length(size_bytes as i64)
            .content_type("application/zip")
            .body(body)
            .send()
            .await
            .map_err(|_| StoreError)?;
        Ok(())
    }

    async fn get(&self, key: &str) -> Result<ObjectStream, StoreError> {
        let output = self
            .client
            .get_object()
            .bucket(&self.bucket)
            .key(key)
            .send()
            .await
            .map_err(|_| StoreError)?;
        let stream = ReaderStream::new(output.body.into_async_read());
        Ok(Box::pin(stream))
    }

    async fn delete(&self, key: &str) -> Result<(), StoreError> {
        self.client
            .delete_object()
            .bucket(&self.bucket)
            .key(key)
            .send()
            .await
            .map_err(|_| StoreError)?;
        Ok(())
    }
}

#[derive(Default, Clone)]
pub struct InMemoryObjectStore {
    objects: Arc<Mutex<HashMap<String, Arc<Vec<u8>>>>>,
    fail_next_put: Arc<AtomicBool>,
    fail_next_delete: Arc<AtomicBool>,
}

impl InMemoryObjectStore {
    pub fn fail_next_put(&self) {
        self.fail_next_put.store(true, Ordering::SeqCst);
    }

    pub fn fail_next_delete(&self) {
        self.fail_next_delete.store(true, Ordering::SeqCst);
    }

    pub fn contains(&self, key: &str) -> bool {
        self.objects
            .lock()
            .map(|objects| objects.contains_key(key))
            .unwrap_or(false)
    }
}

#[async_trait]
impl ObjectStore for InMemoryObjectStore {
    async fn put_file(&self, key: &str, path: &Path, _size_bytes: u64) -> Result<(), StoreError> {
        if self.fail_next_put.swap(false, Ordering::SeqCst) {
            return Err(StoreError);
        }
        let bytes = tokio::fs::read(path).await.map_err(|_| StoreError)?;
        self.objects
            .lock()
            .map_err(|_| StoreError)?
            .insert(key.to_owned(), Arc::new(bytes));
        Ok(())
    }

    async fn get(&self, key: &str) -> Result<ObjectStream, StoreError> {
        let bytes = self
            .objects
            .lock()
            .map_err(|_| StoreError)?
            .get(key)
            .cloned()
            .ok_or(StoreError)?;
        let stream = stream::unfold((bytes, 0usize), |(bytes, offset)| async move {
            if offset >= bytes.len() {
                return None;
            }
            let end = (offset + 64 * 1024).min(bytes.len());
            let chunk = Bytes::copy_from_slice(&bytes[offset..end]);
            Some((Ok(chunk), (bytes, end)))
        });
        Ok(Box::pin(stream))
    }

    async fn delete(&self, key: &str) -> Result<(), StoreError> {
        if self.fail_next_delete.swap(false, Ordering::SeqCst) {
            return Err(StoreError);
        }
        self.objects.lock().map_err(|_| StoreError)?.remove(key);
        Ok(())
    }
}

pub struct UnavailableObjectStore;

#[async_trait]
impl ObjectStore for UnavailableObjectStore {
    async fn put_file(&self, _key: &str, _path: &Path, _size_bytes: u64) -> Result<(), StoreError> {
        Err(StoreError)
    }

    async fn get(&self, _key: &str) -> Result<ObjectStream, StoreError> {
        Err(StoreError)
    }

    async fn delete(&self, _key: &str) -> Result<(), StoreError> {
        Err(StoreError)
    }
}
