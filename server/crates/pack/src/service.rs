use std::{sync::Arc, time::Duration};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use serde::Serialize;
use sqlx::{
    pool::PoolConnection,
    postgres::{PgAdvisoryLock, PgAdvisoryLockGuard, PgAdvisoryLockKey},
    Connection, FromRow, PgConnection, PgPool, Postgres, Transaction,
};
use time::{format_description::well_known::Rfc3339, Duration as TimeDuration, OffsetDateTime};
use uuid::Uuid;

use crate::{
    archive::StagedArchive,
    error::{PackError, PackResult},
    store::{ObjectStore, ObjectStream},
};

const CLEANUP_LEASE: TimeDuration = TimeDuration::seconds(60);

#[derive(Debug, Clone, Serialize)]
pub struct PackMetadata {
    pub pack_id: Uuid,
    pub revision: i64,
    pub etag: String,
    pub size_bytes: u64,
    pub sha256: String,
    pub name: String,
    pub minecraft_version: String,
    pub loader: String,
    pub created_at: String,
    pub updated_at: String,
}

#[derive(Debug, Serialize)]
pub struct ListPacks {
    pub items: Vec<PackMetadata>,
    pub next_cursor: Option<String>,
}

#[derive(Clone)]
pub struct PackService {
    pool: PgPool,
    store: Arc<dyn ObjectStore>,
    cleanup_delay: Duration,
}

#[derive(Debug, FromRow)]
struct PackRow {
    owner_id: Uuid,
    pack_id: Uuid,
    revision: i64,
    etag: String,
    s3_key: String,
    size_bytes: i64,
    sha256: String,
    name: String,
    minecraft_version: String,
    loader: String,
    created_at: OffsetDateTime,
    updated_at: OffsetDateTime,
}

#[derive(Debug, Clone, Copy)]
struct Cursor {
    updated_at: OffsetDateTime,
    pack_id: Uuid,
}

impl PackService {
    pub fn new(pool: PgPool, store: Arc<dyn ObjectStore>, cleanup_delay: Duration) -> Self {
        Self {
            pool,
            store,
            cleanup_delay,
        }
    }

    pub async fn list(
        &self,
        owner_id: Uuid,
        limit: usize,
        cursor: Option<&str>,
    ) -> PackResult<ListPacks> {
        let after = cursor.map(decode_cursor).transpose()?;
        let rows = sqlx::query_as::<_, PackRow>(
            "SELECT owner_id, pack_id, revision, etag, s3_key, size_bytes, sha256, name, \
             minecraft_version, loader, created_at, updated_at FROM packs \
             WHERE owner_id = $1 AND ($2::timestamptz IS NULL OR updated_at < $2 \
             OR (updated_at = $2 AND pack_id > $3)) \
             ORDER BY updated_at DESC, pack_id ASC LIMIT $4",
        )
        .bind(owner_id)
        .bind(after.map(|value| value.updated_at))
        .bind(after.map(|value| value.pack_id))
        .bind((limit + 1) as i64)
        .fetch_all(&self.pool)
        .await?;

        let has_more = rows.len() > limit;
        let page_rows = rows.into_iter().take(limit).collect::<Vec<_>>();
        let next_cursor = if has_more {
            page_rows.last().map(encode_cursor).transpose()?
        } else {
            None
        };
        let items = page_rows
            .into_iter()
            .map(metadata_from_row)
            .collect::<PackResult<Vec<_>>>()?;
        Ok(ListPacks { items, next_cursor })
    }

    pub async fn exists(&self, owner_id: Uuid, pack_id: Uuid) -> PackResult<bool> {
        sqlx::query_scalar::<_, bool>(
            "SELECT EXISTS (SELECT 1 FROM packs WHERE owner_id = $1 AND pack_id = $2)",
        )
        .bind(owner_id)
        .bind(pack_id)
        .fetch_one(&self.pool)
        .await
        .map_err(Into::into)
    }

    pub async fn metadata(&self, owner_id: Uuid, pack_id: Uuid) -> PackResult<PackMetadata> {
        let row = sqlx::query_as::<_, PackRow>(
            "SELECT owner_id, pack_id, revision, etag, s3_key, size_bytes, sha256, name, \
             minecraft_version, loader, created_at, updated_at FROM packs \
             WHERE owner_id = $1 AND pack_id = $2",
        )
        .bind(owner_id)
        .bind(pack_id)
        .fetch_optional(&self.pool)
        .await?
        .ok_or_else(PackError::not_found)?;
        metadata_from_row(row)
    }

    pub async fn ensure_etag(&self, owner_id: Uuid, pack_id: Uuid, etag: &str) -> PackResult<()> {
        let current = sqlx::query_scalar::<_, String>(
            "SELECT etag FROM packs WHERE owner_id = $1 AND pack_id = $2",
        )
        .bind(owner_id)
        .bind(pack_id)
        .fetch_optional(&self.pool)
        .await?
        .ok_or_else(PackError::not_found)?;
        if current != etag {
            return Err(PackError::precondition_failed());
        }
        Ok(())
    }

    pub async fn create(
        &self,
        owner_id: Uuid,
        pack_id: Uuid,
        archive: StagedArchive,
    ) -> PackResult<PackMetadata> {
        let revision = 1;
        let etag = new_etag();
        let s3_key = object_key(owner_id, pack_id, revision, &etag);
        let size_bytes =
            i64::try_from(archive.size_bytes).map_err(|_| PackError::payload_too_large())?;
        let now = OffsetDateTime::now_utc();
        let mut upload_lock = lock_upload_object_key(&self.pool, &s3_key).await?;
        let result = async {
            enqueue_upload_cleanup(
                &mut upload_lock,
                &s3_key,
                now + to_time_duration(self.cleanup_delay),
                now,
            )
            .await?;
            self.store
                .put_file(&s3_key, archive.file.path(), archive.size_bytes)
                .await
                .map_err(|_| PackError::storage_unavailable())?;

            let mut transaction = (*upload_lock).begin().await?;
            let row = sqlx::query_as::<_, PackRow>(
                "INSERT INTO packs (owner_id, pack_id, revision, etag, s3_key, size_bytes, \
                 sha256, name, minecraft_version, loader, created_at, updated_at) \
                 VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $11) \
                 ON CONFLICT (owner_id, pack_id) DO NOTHING \
                 RETURNING owner_id, pack_id, revision, etag, s3_key, size_bytes, sha256, name, \
                 minecraft_version, loader, created_at, updated_at",
            )
            .bind(owner_id)
            .bind(pack_id)
            .bind(revision)
            .bind(&etag)
            .bind(&s3_key)
            .bind(size_bytes)
            .bind(&archive.sha256)
            .bind(&archive.manifest.name)
            .bind(&archive.manifest.minecraft_version)
            .bind(&archive.manifest.loader)
            .bind(now)
            .fetch_optional(&mut *transaction)
            .await?;

            if let Some(row) = row {
                remove_cleanup(&mut transaction, &s3_key).await?;
                transaction.commit().await?;
                Ok(row)
            } else {
                transaction.commit().await?;
                Err(PackError::precondition_failed())
            }
        }
        .await;
        metadata_from_row(finish_upload(upload_lock, result).await?)
    }

    pub async fn archive(
        &self,
        owner_id: Uuid,
        pack_id: Uuid,
    ) -> PackResult<(PackMetadata, ObjectStream)> {
        let row = sqlx::query_as::<_, PackRow>(
            "SELECT owner_id, pack_id, revision, etag, s3_key, size_bytes, sha256, name, \
             minecraft_version, loader, created_at, updated_at FROM packs \
             WHERE owner_id = $1 AND pack_id = $2",
        )
        .bind(owner_id)
        .bind(pack_id)
        .fetch_optional(&self.pool)
        .await?
        .ok_or_else(PackError::not_found)?;
        let stream = self
            .store
            .get(&row.s3_key)
            .await
            .map_err(|_| PackError::storage_unavailable())?;
        Ok((metadata_from_row(row)?, stream))
    }

    pub async fn replace(
        &self,
        owner_id: Uuid,
        pack_id: Uuid,
        expected_etag: &str,
        archive: StagedArchive,
    ) -> PackResult<PackMetadata> {
        let snapshot = sqlx::query_as::<_, PackRow>(
            "SELECT owner_id, pack_id, revision, etag, s3_key, size_bytes, sha256, name, \
             minecraft_version, loader, created_at, updated_at FROM packs \
             WHERE owner_id = $1 AND pack_id = $2",
        )
        .bind(owner_id)
        .bind(pack_id)
        .fetch_optional(&self.pool)
        .await?
        .ok_or_else(PackError::not_found)?;
        if snapshot.etag != expected_etag {
            return Err(PackError::precondition_failed());
        }
        let revision = snapshot
            .revision
            .checked_add(1)
            .ok_or_else(PackError::internal)?;
        let etag = new_etag();
        let s3_key = object_key(owner_id, pack_id, revision, &etag);
        let size_bytes =
            i64::try_from(archive.size_bytes).map_err(|_| PackError::payload_too_large())?;
        let now = OffsetDateTime::now_utc();
        let mut upload_lock = lock_upload_object_key(&self.pool, &s3_key).await?;
        let result = async {
            enqueue_upload_cleanup(
                &mut upload_lock,
                &s3_key,
                now + to_time_duration(self.cleanup_delay),
                now,
            )
            .await?;
            self.store
                .put_file(&s3_key, archive.file.path(), archive.size_bytes)
                .await
                .map_err(|_| PackError::storage_unavailable())?;

            let mut transaction = (*upload_lock).begin().await?;
            let current = sqlx::query_as::<_, PackRow>(
                "SELECT owner_id, pack_id, revision, etag, s3_key, size_bytes, sha256, name, \
                 minecraft_version, loader, created_at, updated_at FROM packs \
                 WHERE owner_id = $1 AND pack_id = $2 FOR UPDATE",
            )
            .bind(owner_id)
            .bind(pack_id)
            .fetch_optional(&mut *transaction)
            .await?;

            if let Some(current) = current {
                if current.revision != snapshot.revision || current.etag != expected_etag {
                    transaction.commit().await?;
                    return Err(PackError::precondition_failed());
                }

                let row = sqlx::query_as::<_, PackRow>(
                    "UPDATE packs SET revision = $3, etag = $4, s3_key = $5, size_bytes = $6, \
                     sha256 = $7, name = $8, minecraft_version = $9, loader = $10, updated_at = $11 \
                     WHERE owner_id = $1 AND pack_id = $2 AND revision = $12 AND etag = $13 \
                     RETURNING owner_id, pack_id, revision, etag, s3_key, size_bytes, sha256, name, \
                     minecraft_version, loader, created_at, updated_at",
                )
                .bind(owner_id)
                .bind(pack_id)
                .bind(revision)
                .bind(&etag)
                .bind(&s3_key)
                .bind(size_bytes)
                .bind(&archive.sha256)
                .bind(&archive.manifest.name)
                .bind(&archive.manifest.minecraft_version)
                .bind(&archive.manifest.loader)
                .bind(now)
                .bind(snapshot.revision)
                .bind(expected_etag)
                .fetch_optional(&mut *transaction)
                .await?;

                if let Some(row) = row {
                    remove_cleanup(&mut transaction, &s3_key).await?;
                    enqueue_cleanup(
                        &mut transaction,
                        &current.s3_key,
                        now + to_time_duration(self.cleanup_delay),
                        now,
                    )
                    .await?;
                    transaction.commit().await?;
                    Ok(row)
                } else {
                    transaction.commit().await?;
                    Err(PackError::precondition_failed())
                }
            } else {
                transaction.commit().await?;
                Err(PackError::not_found())
            }
        }
        .await;
        metadata_from_row(finish_upload(upload_lock, result).await?)
    }

    pub async fn delete(
        &self,
        owner_id: Uuid,
        pack_id: Uuid,
        expected_etag: &str,
    ) -> PackResult<()> {
        let now = OffsetDateTime::now_utc();
        let mut transaction = self.pool.begin().await?;
        let row = sqlx::query_as::<_, PackRow>(
            "SELECT owner_id, pack_id, revision, etag, s3_key, size_bytes, sha256, name, \
             minecraft_version, loader, created_at, updated_at FROM packs \
             WHERE owner_id = $1 AND pack_id = $2 FOR UPDATE",
        )
        .bind(owner_id)
        .bind(pack_id)
        .fetch_optional(&mut *transaction)
        .await?
        .ok_or_else(PackError::not_found)?;
        if row.etag != expected_etag {
            return Err(PackError::precondition_failed());
        }
        sqlx::query("DELETE FROM packs WHERE owner_id = $1 AND pack_id = $2")
            .bind(owner_id)
            .bind(pack_id)
            .execute(&mut *transaction)
            .await?;
        enqueue_cleanup(
            &mut transaction,
            &row.s3_key,
            now + to_time_duration(self.cleanup_delay),
            now,
        )
        .await?;
        transaction.commit().await?;
        Ok(())
    }

    pub async fn cleanup_once(&self) -> PackResult<()> {
        let mut skipped_locked_ids = Vec::new();
        loop {
            let now = OffsetDateTime::now_utc();
            let candidate = sqlx::query_as::<_, (i64, String)>(
                "SELECT id, s3_key FROM pack_cleanup WHERE eligible_at <= $1 \
                 AND id != ALL($2) \
                 ORDER BY eligible_at, id LIMIT 1",
            )
            .bind(now)
            .bind(&skipped_locked_ids)
            .fetch_optional(&self.pool)
            .await?;
            let Some((id, key)) = candidate else {
                return Ok(());
            };
            let mut transaction = self.pool.begin().await?;
            if !try_lock_cleanup_object_key(&mut transaction, &key).await? {
                transaction.rollback().await?;
                skipped_locked_ids.push(id);
                continue;
            }
            let current_attempts = sqlx::query_scalar::<_, i32>(
                "SELECT attempts FROM pack_cleanup WHERE id = $1 AND eligible_at <= $2 FOR UPDATE",
            )
            .bind(id)
            .bind(OffsetDateTime::now_utc())
            .fetch_optional(&mut *transaction)
            .await?;
            let Some(attempts) = current_attempts else {
                transaction.commit().await?;
                continue;
            };
            sqlx::query("UPDATE pack_cleanup SET eligible_at = $2 WHERE id = $1")
                .bind(id)
                .bind(OffsetDateTime::now_utc() + CLEANUP_LEASE)
                .execute(&mut *transaction)
                .await?;
            transaction.commit().await?;

            match self.store.delete(&key).await {
                Ok(()) => {
                    sqlx::query("DELETE FROM pack_cleanup WHERE id = $1")
                        .bind(id)
                        .execute(&self.pool)
                        .await?;
                }
                Err(_) => {
                    let next_attempts = attempts.saturating_add(1);
                    let backoff = (60i64 * (1i64 << attempts.clamp(0, 6))).min(3_600);
                    let error = "object store delete failed";
                    sqlx::query(
                        "UPDATE pack_cleanup SET attempts = $2, last_error = $3, \
                         eligible_at = $4 WHERE id = $1",
                    )
                    .bind(id)
                    .bind(next_attempts)
                    .bind(error)
                    .bind(OffsetDateTime::now_utc() + TimeDuration::seconds(backoff))
                    .execute(&self.pool)
                    .await?;
                    tracing::warn!(
                        cleanup_id = id,
                        attempts = next_attempts,
                        "pack object cleanup will retry"
                    );
                }
            }
        }
    }
}

async fn lock_upload_object_key(
    pool: &PgPool,
    s3_key: &str,
) -> PackResult<PgAdvisoryLockGuard<PoolConnection<Postgres>>> {
    let mut connection = pool.acquire().await?;
    let lock_key = sqlx::query_scalar::<_, i64>("SELECT hashtextextended($1, 0)")
        .bind(s3_key)
        .fetch_one(&mut *connection)
        .await?;
    Ok(
        PgAdvisoryLock::with_key(PgAdvisoryLockKey::BigInt(lock_key))
            .acquire(connection)
            .await?,
    )
}

async fn finish_upload<T>(
    upload_lock: PgAdvisoryLockGuard<PoolConnection<Postgres>>,
    result: PackResult<T>,
) -> PackResult<T> {
    let unlock_result = upload_lock.release_now().await.map(|_| ());
    match result {
        Err(error) => Err(error),
        Ok(value) => {
            unlock_result?;
            Ok(value)
        }
    }
}

async fn try_lock_cleanup_object_key(
    transaction: &mut Transaction<'_, Postgres>,
    s3_key: &str,
) -> PackResult<bool> {
    sqlx::query_scalar::<_, bool>("SELECT pg_try_advisory_xact_lock(hashtextextended($1, 0))")
        .bind(s3_key)
        .fetch_one(&mut **transaction)
        .await
        .map_err(Into::into)
}

pub fn object_key(owner_id: Uuid, pack_id: Uuid, revision: i64, etag: &str) -> String {
    format!("packs/{owner_id}/{pack_id}/r{revision}-{etag}.zip")
}

pub fn new_etag() -> String {
    Uuid::new_v4().to_string()
}

async fn enqueue_cleanup(
    transaction: &mut sqlx::Transaction<'_, sqlx::Postgres>,
    key: &str,
    eligible_at: OffsetDateTime,
    created_at: OffsetDateTime,
) -> PackResult<()> {
    sqlx::query(
        "INSERT INTO pack_cleanup (s3_key, eligible_at, attempts, created_at) \
         VALUES ($1, $2, 0, $3) ON CONFLICT (s3_key) DO NOTHING",
    )
    .bind(key)
    .bind(eligible_at)
    .bind(created_at)
    .execute(&mut **transaction)
    .await?;
    Ok(())
}

async fn enqueue_upload_cleanup(
    connection: &mut PgConnection,
    key: &str,
    eligible_at: OffsetDateTime,
    created_at: OffsetDateTime,
) -> PackResult<()> {
    let mut transaction = connection.begin().await?;
    enqueue_cleanup(&mut transaction, key, eligible_at, created_at).await?;
    transaction.commit().await?;
    Ok(())
}

async fn remove_cleanup(
    transaction: &mut sqlx::Transaction<'_, sqlx::Postgres>,
    key: &str,
) -> PackResult<()> {
    sqlx::query("DELETE FROM pack_cleanup WHERE s3_key = $1")
        .bind(key)
        .execute(&mut **transaction)
        .await?;
    Ok(())
}

fn metadata_from_row(row: PackRow) -> PackResult<PackMetadata> {
    let _owner_id = row.owner_id;
    let size_bytes = u64::try_from(row.size_bytes).map_err(|_| PackError::internal())?;
    Ok(PackMetadata {
        pack_id: row.pack_id,
        revision: row.revision,
        etag: row.etag,
        size_bytes,
        sha256: row.sha256,
        name: row.name,
        minecraft_version: row.minecraft_version,
        loader: row.loader,
        created_at: row
            .created_at
            .format(&Rfc3339)
            .map_err(|_| PackError::internal())?,
        updated_at: row
            .updated_at
            .format(&Rfc3339)
            .map_err(|_| PackError::internal())?,
    })
}

fn encode_cursor(row: &PackRow) -> PackResult<String> {
    let value = format!(
        "{}:{}",
        row.updated_at.unix_timestamp_nanos() / 1_000,
        row.pack_id
    );
    Ok(URL_SAFE_NO_PAD.encode(value))
}

fn decode_cursor(value: &str) -> PackResult<Cursor> {
    let decoded = URL_SAFE_NO_PAD
        .decode(value)
        .map_err(|_| PackError::bad_request())?;
    let decoded = std::str::from_utf8(&decoded).map_err(|_| PackError::bad_request())?;
    let (timestamp, id) = decoded.split_once(':').ok_or_else(PackError::bad_request)?;
    let micros = timestamp
        .parse::<i128>()
        .map_err(|_| PackError::bad_request())?;
    let nanos = micros
        .checked_mul(1_000)
        .ok_or_else(PackError::bad_request)?;
    let updated_at =
        OffsetDateTime::from_unix_timestamp_nanos(nanos).map_err(|_| PackError::bad_request())?;
    let pack_id = Uuid::parse_str(id).map_err(|_| PackError::bad_request())?;
    Ok(Cursor {
        updated_at,
        pack_id,
    })
}

fn to_time_duration(duration: Duration) -> TimeDuration {
    TimeDuration::seconds(duration.as_secs().min(i64::MAX as u64) as i64)
}

#[cfg(test)]
mod tests {
    use std::{
        env, fs,
        path::Path,
        sync::{
            atomic::{AtomicUsize, Ordering},
            Arc,
        },
        time::Duration,
    };

    use async_trait::async_trait;
    use sqlx::postgres::PgPoolOptions;
    use tokio::sync::{Notify, Semaphore};
    use uuid::Uuid;

    use crate::{
        archive::{ManifestMetadata, StagedArchive},
        store::{ObjectStore, ObjectStream, StoreError},
    };

    use super::{new_etag, object_key, PackService};

    struct GatedObjectStore {
        started: AtomicUsize,
        two_uploads_started: Notify,
        release: Semaphore,
    }

    impl GatedObjectStore {
        fn new() -> Self {
            Self {
                started: AtomicUsize::new(0),
                two_uploads_started: Notify::new(),
                release: Semaphore::new(0),
            }
        }
    }

    #[async_trait]
    impl ObjectStore for GatedObjectStore {
        async fn put_file(
            &self,
            _key: &str,
            _path: &Path,
            _size_bytes: u64,
        ) -> Result<(), StoreError> {
            let index = self.started.fetch_add(1, Ordering::SeqCst);
            if index < 2 {
                if index == 1 {
                    self.two_uploads_started.notify_one();
                }
                self.release
                    .acquire()
                    .await
                    .map_err(|_| StoreError)?
                    .forget();
            }
            Ok(())
        }

        async fn get(&self, _key: &str) -> Result<ObjectStream, StoreError> {
            Err(StoreError)
        }

        async fn delete(&self, _key: &str) -> Result<(), StoreError> {
            Ok(())
        }
    }

    fn staged_archive(name: &str) -> StagedArchive {
        let file = tempfile::NamedTempFile::new().expect("create staged archive");
        fs::write(file.path(), b"test archive").expect("write staged archive");
        StagedArchive {
            file,
            size_bytes: 12,
            sha256: "test-digest".to_owned(),
            manifest: ManifestMetadata {
                name: name.to_owned(),
                minecraft_version: "1.21.4".to_owned(),
                loader: "fabric".to_owned(),
            },
        }
    }

    #[tokio::test]
    async fn concurrent_uploads_use_one_pool_connection_and_allow_metadata_queries() {
        let Ok(database_url) = env::var("HAKO_TEST_DATABASE_URL") else {
            println!(
                "SKIP: HAKO_TEST_DATABASE_URL is unset; Pack PostgreSQL pool coverage was not run."
            );
            return;
        };

        let pool = PgPoolOptions::new()
            .max_connections(2)
            .acquire_timeout(Duration::from_secs(5))
            .connect(&database_url)
            .await
            .expect("connect Pack pool test database");
        sqlx::migrate!("./migrations")
            .run(&pool)
            .await
            .expect("apply Pack migrations to the isolated test database");

        let owner_id = Uuid::new_v4();
        let gated_store = Arc::new(GatedObjectStore::new());
        let service = PackService::new(pool.clone(), gated_store.clone(), Duration::ZERO);
        let mut uploads = tokio::task::JoinSet::new();
        for index in 0..4 {
            let service = service.clone();
            uploads.spawn(async move {
                service
                    .create(
                        owner_id,
                        Uuid::new_v4(),
                        staged_archive(&format!("Concurrent pack {index}")),
                    )
                    .await
            });
        }

        if tokio::time::timeout(
            Duration::from_secs(5),
            gated_store.two_uploads_started.notified(),
        )
        .await
        .is_err()
        {
            if let Some(result) = uploads.try_join_next() {
                panic!("an upload ended before the object store gate opened: {result:?}");
            }
            gated_store.release.add_permits(2);
            uploads.abort_all();
            while uploads.join_next().await.is_some() {}
            pool.close().await;
            panic!("two uploads should reach object storage with a two-connection pool");
        }

        let metadata_service = service.clone();
        let metadata_query =
            tokio::spawn(async move { metadata_service.list(owner_id, 50, None).await });
        tokio::task::yield_now().await;
        gated_store.release.add_permits(2);

        while let Some(result) = tokio::time::timeout(Duration::from_secs(5), uploads.join_next())
            .await
            .expect("parallel uploads should publish without nested pool acquisition")
        {
            result
                .expect("join concurrent upload")
                .expect("publish concurrent upload");
        }
        metadata_query
            .await
            .expect("join parallel metadata query")
            .expect("metadata query succeeds");
        pool.close().await;
    }

    #[test]
    fn object_keys_are_scoped_by_owner_pack_revision_and_etag() {
        let owner = Uuid::parse_str("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa").unwrap();
        let pack = Uuid::parse_str("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb").unwrap();
        assert_eq!(
            object_key(owner, pack, 3, "etag-value"),
            "packs/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/r3-etag-value.zip"
        );
    }

    #[test]
    fn etags_are_unique_and_valid_uuid_strings() {
        let first = new_etag();
        let second = new_etag();
        assert_ne!(first, second);
        assert!(Uuid::parse_str(&first).is_ok());
        assert!(Uuid::parse_str(&second).is_ok());
    }
}
