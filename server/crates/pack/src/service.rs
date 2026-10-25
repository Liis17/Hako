use std::{sync::Arc, time::Duration};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use serde::Serialize;
use sqlx::{FromRow, PgPool, Postgres, Transaction};
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
        let mut upload_lock = self.pool.begin().await?;
        lock_object_key(&mut upload_lock, &s3_key).await?;
        enqueue_upload_cleanup(
            &self.pool,
            &s3_key,
            now + to_time_duration(self.cleanup_delay),
            now,
        )
        .await?;
        self.store
            .put_file(&s3_key, archive.file.path(), archive.size_bytes)
            .await
            .map_err(|_| PackError::storage_unavailable())?;

        let mut transaction = self.pool.begin().await?;
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

        let Some(row) = row else {
            transaction.commit().await?;
            return Err(PackError::precondition_failed());
        };
        remove_cleanup(&mut transaction, &s3_key).await?;
        transaction.commit().await?;
        upload_lock.commit().await?;
        metadata_from_row(row)
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
        let mut upload_lock = self.pool.begin().await?;
        lock_object_key(&mut upload_lock, &s3_key).await?;
        enqueue_upload_cleanup(
            &self.pool,
            &s3_key,
            now + to_time_duration(self.cleanup_delay),
            now,
        )
        .await?;
        self.store
            .put_file(&s3_key, archive.file.path(), archive.size_bytes)
            .await
            .map_err(|_| PackError::storage_unavailable())?;

        let mut transaction = self.pool.begin().await?;
        let current = sqlx::query_as::<_, PackRow>(
            "SELECT owner_id, pack_id, revision, etag, s3_key, size_bytes, sha256, name, \
             minecraft_version, loader, created_at, updated_at FROM packs \
             WHERE owner_id = $1 AND pack_id = $2 FOR UPDATE",
        )
        .bind(owner_id)
        .bind(pack_id)
        .fetch_optional(&mut *transaction)
        .await?;
        let Some(current) = current else {
            transaction.commit().await?;
            return Err(PackError::not_found());
        };
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
        let Some(row) = row else {
            transaction.commit().await?;
            return Err(PackError::precondition_failed());
        };
        remove_cleanup(&mut transaction, &s3_key).await?;
        enqueue_cleanup(
            &mut transaction,
            &current.s3_key,
            now + to_time_duration(self.cleanup_delay),
            now,
        )
        .await?;
        transaction.commit().await?;
        upload_lock.commit().await?;
        metadata_from_row(row)
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
        loop {
            let now = OffsetDateTime::now_utc();
            let candidate = sqlx::query_as::<_, (i64, String)>(
                "SELECT id, s3_key FROM pack_cleanup WHERE eligible_at <= $1 \
                 ORDER BY eligible_at, id LIMIT 1",
            )
            .bind(now)
            .fetch_optional(&self.pool)
            .await?;
            let Some((id, key)) = candidate else {
                return Ok(());
            };
            let mut transaction = self.pool.begin().await?;
            lock_object_key(&mut transaction, &key).await?;
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

async fn lock_object_key(
    transaction: &mut Transaction<'_, Postgres>,
    s3_key: &str,
) -> PackResult<()> {
    sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))")
        .bind(s3_key)
        .execute(&mut **transaction)
        .await?;
    Ok(())
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
    pool: &PgPool,
    key: &str,
    eligible_at: OffsetDateTime,
    created_at: OffsetDateTime,
) -> PackResult<()> {
    let mut transaction = pool.begin().await?;
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
    use uuid::Uuid;

    use super::{new_etag, object_key};

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
