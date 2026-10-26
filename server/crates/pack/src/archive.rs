use std::{
    collections::HashSet,
    env,
    fs::File,
    io::{self, ErrorKind, Read, Seek, SeekFrom},
    path::Path,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::Duration,
};

use axum::body::Body;
use futures_util::StreamExt;
use serde_json::Value;
use sha2::{Digest, Sha256};
use tempfile::NamedTempFile;
use tokio::io::AsyncWriteExt;
use tokio::sync::Semaphore;
use zip::ZipArchive;

use crate::error::{PackError, PackResult};

pub const MAX_DOWNLOAD_DURATION: Duration = Duration::from_secs(60 * 60);
const DEFAULT_MAX_ARCHIVE_BYTES: u64 = 1_073_741_824;
const DEFAULT_MAX_UNCOMPRESSED_BYTES: u64 = 4_294_967_296;
const DEFAULT_MAX_ENTRIES: usize = 10_000;
const DEFAULT_MAX_MANIFEST_BYTES: u64 = 1_048_576;
const DEFAULT_VALIDATION_CONCURRENCY: usize = 2;
const DEFAULT_VALIDATION_TIMEOUT: Duration = Duration::from_secs(900);
const VALIDATION_QUEUE_TIMEOUT: Duration = Duration::from_secs(1);
const MANIFEST_SCHEMA: &str = include_str!("../../../docs/hakopack-manifest.schema.json");
const PNG_SIGNATURE: &[u8; 8] = b"\x89PNG\r\n\x1a\n";

#[derive(Clone, Debug)]
pub struct PackLimits {
    pub max_archive_bytes: u64,
    pub max_uncompressed_bytes: u64,
    pub max_entries: usize,
    pub max_manifest_bytes: u64,
    validation_semaphore: Arc<Semaphore>,
    validation_timeout: Duration,
}

impl Default for PackLimits {
    fn default() -> Self {
        Self {
            max_archive_bytes: DEFAULT_MAX_ARCHIVE_BYTES,
            max_uncompressed_bytes: DEFAULT_MAX_UNCOMPRESSED_BYTES,
            max_entries: DEFAULT_MAX_ENTRIES,
            max_manifest_bytes: DEFAULT_MAX_MANIFEST_BYTES,
            validation_semaphore: Arc::new(Semaphore::new(DEFAULT_VALIDATION_CONCURRENCY)),
            validation_timeout: DEFAULT_VALIDATION_TIMEOUT,
        }
    }
}

impl PackLimits {
    pub fn from_env() -> Result<Self, io::Error> {
        let defaults = Self::default();
        let validation_concurrency = limit_usize(
            "HAKO_PACK_VALIDATION_CONCURRENCY",
            DEFAULT_VALIDATION_CONCURRENCY,
        )?;
        if validation_concurrency > Semaphore::MAX_PERMITS {
            return Err(io::Error::new(
                ErrorKind::InvalidInput,
                "HAKO_PACK_VALIDATION_CONCURRENCY exceeds the supported maximum",
            ));
        }
        Ok(Self {
            max_archive_bytes: limit_u64(
                "HAKO_PACK_MAX_ARCHIVE_BYTES",
                defaults.max_archive_bytes,
            )?,
            max_uncompressed_bytes: limit_u64(
                "HAKO_PACK_MAX_UNCOMPRESSED_BYTES",
                defaults.max_uncompressed_bytes,
            )?,
            max_entries: limit_usize("HAKO_PACK_MAX_ENTRIES", defaults.max_entries)?,
            max_manifest_bytes: limit_u64(
                "HAKO_PACK_MAX_MANIFEST_BYTES",
                defaults.max_manifest_bytes,
            )?,
            validation_semaphore: Arc::new(Semaphore::new(validation_concurrency)),
            validation_timeout: Duration::from_secs(limit_u64(
                "HAKO_PACK_VALIDATION_TIMEOUT_SECS",
                DEFAULT_VALIDATION_TIMEOUT.as_secs(),
            )?),
        })
    }
}

#[derive(Debug)]
pub struct ManifestMetadata {
    pub name: String,
    pub minecraft_version: String,
    pub loader: String,
}

pub struct StagedArchive {
    pub file: NamedTempFile,
    pub size_bytes: u64,
    pub sha256: String,
    pub manifest: ManifestMetadata,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ArchiveError {
    Invalid,
    TooLarge,
    TimedOut,
    Cancelled,
}

struct ValidationCancellationGuard(Arc<AtomicBool>);

impl Drop for ValidationCancellationGuard {
    fn drop(&mut self) {
        self.0.store(true, Ordering::Relaxed);
    }
}

pub async fn receive_archive(
    body: Body,
    content_length: Option<u64>,
    limits: &PackLimits,
) -> PackResult<StagedArchive> {
    if content_length.is_some_and(|length| length > limits.max_archive_bytes) {
        return Err(PackError::payload_too_large());
    }

    let temp = NamedTempFile::new().map_err(|_| PackError::internal())?;
    let file = temp.reopen().map_err(|_| PackError::internal())?;
    let mut file = tokio::fs::File::from_std(file);
    let mut hasher = Sha256::new();
    let mut size_bytes = 0u64;
    let mut stream = body.into_data_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|_| PackError::bad_request())?;
        size_bytes = size_bytes
            .checked_add(chunk.len() as u64)
            .ok_or_else(PackError::payload_too_large)?;
        if size_bytes > limits.max_archive_bytes {
            return Err(PackError::payload_too_large());
        }
        hasher.update(&chunk);
        file.write_all(&chunk)
            .await
            .map_err(|_| PackError::internal())?;
    }
    file.flush().await.map_err(|_| PackError::internal())?;
    drop(file);

    let permit = tokio::time::timeout(
        VALIDATION_QUEUE_TIMEOUT,
        Arc::clone(&limits.validation_semaphore).acquire_owned(),
    )
    .await
    .map_err(|_| PackError::validation_busy())?
    .map_err(|_| PackError::validation_busy())?;
    let cancellation = Arc::new(AtomicBool::new(false));
    let cancellation_guard = ValidationCancellationGuard(Arc::clone(&cancellation));
    let validation_limits = limits.clone();
    let validation_result = tokio::task::spawn_blocking(move || {
        let _permit = permit;
        let manifest = validate_archive(temp.path(), &validation_limits, &cancellation)?;
        Ok::<_, ArchiveError>((temp, manifest))
    })
    .await;
    drop(cancellation_guard);
    let (temp, manifest) = validation_result
        .map_err(|_| PackError::internal())?
        .map_err(|error| match error {
            ArchiveError::TooLarge => PackError::payload_too_large(),
            ArchiveError::Invalid | ArchiveError::TimedOut | ArchiveError::Cancelled => {
                PackError::invalid_archive()
            }
        })?;

    Ok(StagedArchive {
        file: temp,
        size_bytes,
        sha256: hex_digest(&hasher.finalize()),
        manifest,
    })
}

fn hex_digest(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        result.push(HEX[usize::from(byte >> 4)] as char);
        result.push(HEX[usize::from(byte & 0x0f)] as char);
    }
    result
}

fn validate_archive(
    path: &Path,
    limits: &PackLimits,
    cancelled: &AtomicBool,
) -> Result<ManifestMetadata, ArchiveError> {
    let validation_started = std::time::Instant::now();
    check_validation_budget(validation_started, limits.validation_timeout, cancelled)?;
    let mut file = File::open(path).map_err(|_| ArchiveError::Invalid)?;
    if declared_entry_count(&mut file)? > limits.max_entries {
        return Err(ArchiveError::TooLarge);
    }
    let mut archive = ZipArchive::new(file).map_err(|_| ArchiveError::Invalid)?;
    check_validation_budget(validation_started, limits.validation_timeout, cancelled)?;
    let mut header_reader = File::open(path).map_err(|_| ArchiveError::Invalid)?;
    if archive.len() > limits.max_entries {
        return Err(ArchiveError::TooLarge);
    }

    let mut paths = HashSet::with_capacity(archive.len());
    let mut uncompressed_bytes = 0u64;
    let mut manifest_bytes = None;
    let mut icon_is_png = None;

    for index in 0..archive.len() {
        check_validation_budget(validation_started, limits.validation_timeout, cancelled)?;
        let mut entry = archive.by_index(index).map_err(|_| ArchiveError::Invalid)?;
        let name = entry.name().to_owned();
        let local_header = entry.header_start();
        let central_header = entry.central_header_start();
        validate_entry_headers(
            &mut header_reader,
            local_header,
            central_header,
            entry.name_raw(),
        )?;
        if encrypted_flag(&mut header_reader, local_header.saturating_add(6))?
            || encrypted_flag(&mut header_reader, central_header.saturating_add(8))?
        {
            return Err(ArchiveError::Invalid);
        }
        if entry
            .unix_mode()
            .is_some_and(|mode| mode & 0o170000 == 0o120000)
        {
            return Err(ArchiveError::Invalid);
        }
        validate_path(&name).map_err(|_| ArchiveError::Invalid)?;
        let collision_key = collision_key(&name, entry.is_dir());
        if !paths.insert(collision_key) {
            return Err(ArchiveError::Invalid);
        }

        let is_directory = entry.is_dir();
        if name != "manifest.json" && name != "icon.png" && !name.starts_with("overrides/") {
            return Err(ArchiveError::Invalid);
        }
        if is_directory {
            continue;
        }

        let declared_size = entry.size();
        if name == "manifest.json" && declared_size > limits.max_manifest_bytes {
            return Err(ArchiveError::TooLarge);
        }
        let mut actual_size = 0u64;
        let mut manifest = (name == "manifest.json").then(Vec::new);
        let mut icon_signature = [0u8; 8];
        let mut icon_signature_len = 0usize;
        let mut buffer = [0u8; 8192];
        loop {
            check_validation_budget(validation_started, limits.validation_timeout, cancelled)?;
            let remaining = limits
                .max_uncompressed_bytes
                .saturating_sub(uncompressed_bytes);
            let read_capacity = usize::try_from(remaining.min((buffer.len() - 1) as u64))
                .map_err(|_| ArchiveError::TooLarge)?
                + 1;
            let read = entry
                .read(&mut buffer[..read_capacity])
                .map_err(|_| ArchiveError::Invalid)?;
            if read == 0 {
                break;
            }
            let read = u64::try_from(read).map_err(|_| ArchiveError::TooLarge)?;
            actual_size = actual_size
                .checked_add(read)
                .ok_or(ArchiveError::TooLarge)?;
            uncompressed_bytes = uncompressed_bytes
                .checked_add(read)
                .ok_or(ArchiveError::TooLarge)?;
            if uncompressed_bytes > limits.max_uncompressed_bytes {
                return Err(ArchiveError::TooLarge);
            }
            if name == "manifest.json" && actual_size > limits.max_manifest_bytes {
                return Err(ArchiveError::TooLarge);
            }

            if let Some(manifest) = &mut manifest {
                manifest.extend_from_slice(&buffer[..read as usize]);
            }
            if name == "icon.png" && icon_signature_len < icon_signature.len() {
                let copy_len = (icon_signature.len() - icon_signature_len).min(read as usize);
                icon_signature[icon_signature_len..icon_signature_len + copy_len]
                    .copy_from_slice(&buffer[..copy_len]);
                icon_signature_len += copy_len;
            }
        }
        if actual_size != declared_size {
            return Err(ArchiveError::Invalid);
        }
        if let Some(bytes) = manifest {
            manifest_bytes = Some(bytes);
        }
        if name == "icon.png" {
            icon_is_png =
                Some(icon_signature_len == PNG_SIGNATURE.len() && &icon_signature == PNG_SIGNATURE);
        }
    }

    check_validation_budget(validation_started, limits.validation_timeout, cancelled)?;
    let bytes = manifest_bytes.ok_or(ArchiveError::Invalid)?;
    let manifest_value: Value =
        serde_json::from_slice(&bytes).map_err(|_| ArchiveError::Invalid)?;
    let schema: Value = serde_json::from_str(MANIFEST_SCHEMA).map_err(|_| ArchiveError::Invalid)?;
    let validator = jsonschema::validator_for(&schema).map_err(|_| ArchiveError::Invalid)?;
    if !validator.is_valid(&manifest_value) {
        return Err(ArchiveError::Invalid);
    }
    if manifest_value.get("format_version").and_then(Value::as_u64) != Some(1) {
        return Err(ArchiveError::Invalid);
    }
    check_validation_budget(validation_started, limits.validation_timeout, cancelled)?;
    let read_string = |field: &str| {
        manifest_value
            .get(field)
            .and_then(Value::as_str)
            .map(str::to_owned)
            .ok_or(ArchiveError::Invalid)
    };
    if manifest_value
        .get("icon")
        .and_then(Value::as_str)
        .is_some_and(|icon| icon != "icon.png" || icon_is_png != Some(true))
    {
        return Err(ArchiveError::Invalid);
    }

    Ok(ManifestMetadata {
        name: read_string("name")?,
        minecraft_version: read_string("minecraft_version")?,
        loader: read_string("loader")?,
    })
}

fn declared_entry_count(file: &mut File) -> Result<usize, ArchiveError> {
    const EOCD_SIGNATURE: [u8; 4] = [0x50, 0x4b, 0x05, 0x06];
    const ZIP64_LOCATOR_SIGNATURE: [u8; 4] = [0x50, 0x4b, 0x06, 0x07];
    const ZIP64_EOCD_SIGNATURE: [u8; 4] = [0x50, 0x4b, 0x06, 0x06];
    const EOCD_LENGTH: usize = 22;
    const TAIL_LENGTH: usize = EOCD_LENGTH + u16::MAX as usize;

    let file_length = file
        .seek(SeekFrom::End(0))
        .map_err(|_| ArchiveError::Invalid)?;
    let tail_length =
        usize::try_from(file_length.min(TAIL_LENGTH as u64)).map_err(|_| ArchiveError::Invalid)?;
    let tail_start = file_length - tail_length as u64;
    file.seek(SeekFrom::Start(tail_start))
        .map_err(|_| ArchiveError::Invalid)?;
    let mut tail = vec![0; tail_length];
    file.read_exact(&mut tail)
        .map_err(|_| ArchiveError::Invalid)?;
    if tail.len() < EOCD_LENGTH {
        return Err(ArchiveError::Invalid);
    }
    let eocd = (0..=tail.len().saturating_sub(EOCD_LENGTH))
        .rev()
        .find(|index| {
            tail[*index..*index + 4] == EOCD_SIGNATURE
                && *index
                    + EOCD_LENGTH
                    + u16::from_le_bytes([tail[*index + 20], tail[*index + 21]]) as usize
                    == tail.len()
        })
        .ok_or(ArchiveError::Invalid)?;
    let entries_on_disk = u16::from_le_bytes([tail[eocd + 8], tail[eocd + 9]]);
    let entries = u16::from_le_bytes([tail[eocd + 10], tail[eocd + 11]]);
    if entries != u16::MAX && entries_on_disk != u16::MAX {
        return Ok(usize::from(entries));
    }

    let eocd_offset = tail_start + eocd as u64;
    let locator_offset = eocd_offset.checked_sub(20).ok_or(ArchiveError::Invalid)?;
    file.seek(SeekFrom::Start(locator_offset))
        .map_err(|_| ArchiveError::Invalid)?;
    let mut locator = [0u8; 20];
    file.read_exact(&mut locator)
        .map_err(|_| ArchiveError::Invalid)?;
    if locator[..4] != ZIP64_LOCATOR_SIGNATURE {
        return Err(ArchiveError::Invalid);
    }
    let zip64_eocd_offset = u64::from_le_bytes(
        locator[8..16]
            .try_into()
            .map_err(|_| ArchiveError::Invalid)?,
    );
    file.seek(SeekFrom::Start(zip64_eocd_offset))
        .map_err(|_| ArchiveError::Invalid)?;
    let mut zip64_eocd = [0u8; 56];
    file.read_exact(&mut zip64_eocd)
        .map_err(|_| ArchiveError::Invalid)?;
    if zip64_eocd[..4] != ZIP64_EOCD_SIGNATURE {
        return Err(ArchiveError::Invalid);
    }
    let entries = u64::from_le_bytes(
        zip64_eocd[32..40]
            .try_into()
            .map_err(|_| ArchiveError::Invalid)?,
    );
    usize::try_from(entries).map_err(|_| ArchiveError::TooLarge)
}

fn encrypted_flag(file: &mut File, offset: u64) -> Result<bool, ArchiveError> {
    file.seek(SeekFrom::Start(offset))
        .map_err(|_| ArchiveError::Invalid)?;
    let mut flags = [0u8; 2];
    file.read_exact(&mut flags)
        .map_err(|_| ArchiveError::Invalid)?;
    Ok(u16::from_le_bytes(flags) & 1 != 0)
}

fn validate_entry_headers(
    file: &mut File,
    local_offset: u64,
    central_offset: u64,
    central_name: &[u8],
) -> Result<(), ArchiveError> {
    const LOCAL_SIGNATURE: [u8; 4] = [0x50, 0x4b, 0x03, 0x04];
    const CENTRAL_SIGNATURE: [u8; 4] = [0x50, 0x4b, 0x01, 0x02];

    file.seek(SeekFrom::Start(local_offset))
        .map_err(|_| ArchiveError::Invalid)?;
    let mut local = [0u8; 30];
    file.read_exact(&mut local)
        .map_err(|_| ArchiveError::Invalid)?;
    if local[..4] != LOCAL_SIGNATURE {
        return Err(ArchiveError::Invalid);
    }
    let local_name_len = usize::from(u16::from_le_bytes([local[26], local[27]]));
    let mut local_name = vec![0; local_name_len];
    file.read_exact(&mut local_name)
        .map_err(|_| ArchiveError::Invalid)?;

    file.seek(SeekFrom::Start(central_offset))
        .map_err(|_| ArchiveError::Invalid)?;
    let mut central = [0u8; 46];
    file.read_exact(&mut central)
        .map_err(|_| ArchiveError::Invalid)?;
    if central[..4] != CENTRAL_SIGNATURE
        || local[8..10] != central[10..12]
        || local_name_len != usize::from(u16::from_le_bytes([central[28], central[29]]))
        || local_name != central_name
    {
        return Err(ArchiveError::Invalid);
    }
    let mut central_name_bytes =
        vec![0; usize::from(u16::from_le_bytes([central[28], central[29]]))];
    file.read_exact(&mut central_name_bytes)
        .map_err(|_| ArchiveError::Invalid)?;
    if local_name != central_name_bytes {
        return Err(ArchiveError::Invalid);
    }
    Ok(())
}

fn validate_path(name: &str) -> Result<(), ()> {
    if name.is_empty() || name.contains(['\\', '\0', ':']) || name.starts_with('/') {
        return Err(());
    }
    let path = name.strip_suffix('/').unwrap_or(name);
    if path.is_empty()
        || path.split('/').any(|segment| {
            segment.is_empty()
                || segment == "."
                || segment == ".."
                || segment.ends_with(['.', ' '])
                || is_reserved_windows_name(segment)
        })
    {
        return Err(());
    }
    Ok(())
}

fn is_reserved_windows_name(segment: &str) -> bool {
    let base = segment.split('.').next().unwrap_or(segment);
    ["CON", "PRN", "AUX", "NUL"]
        .iter()
        .any(|reserved| base.eq_ignore_ascii_case(reserved))
        || (base.get(..3).is_some_and(|prefix| {
            prefix.eq_ignore_ascii_case("COM") || prefix.eq_ignore_ascii_case("LPT")
        }) && matches!(
            base.get(3..),
            Some("1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³")
        ))
}

fn check_validation_budget(
    started: std::time::Instant,
    timeout: Duration,
    cancelled: &AtomicBool,
) -> Result<(), ArchiveError> {
    if cancelled.load(Ordering::Relaxed) {
        Err(ArchiveError::Cancelled)
    } else if started.elapsed() >= timeout {
        Err(ArchiveError::TimedOut)
    } else {
        Ok(())
    }
}

fn collision_key(name: &str, is_dir: bool) -> String {
    let normalized = if is_dir {
        name.strip_suffix('/').unwrap_or(name)
    } else {
        name
    }
    .to_lowercase();
    normalized
        .strip_suffix(".disabled")
        .unwrap_or(&normalized)
        .to_owned()
}

fn limit_u64(name: &'static str, default: u64) -> Result<u64, io::Error> {
    parse_env(name, default)
}

fn limit_usize(name: &'static str, default: usize) -> Result<usize, io::Error> {
    parse_env(name, default)
}

fn parse_env<T>(name: &'static str, default: T) -> Result<T, io::Error>
where
    T: std::str::FromStr + PartialOrd + From<u8>,
{
    match env::var(name) {
        Ok(value) => value
            .parse::<T>()
            .ok()
            .filter(|parsed| *parsed >= T::from(1))
            .ok_or_else(|| {
                io::Error::new(
                    ErrorKind::InvalidInput,
                    format!("environment variable {name} must be a positive integer"),
                )
            }),
        Err(env::VarError::NotPresent) => Ok(default),
        Err(env::VarError::NotUnicode(_)) => Err(io::Error::new(
            ErrorKind::InvalidInput,
            format!("environment variable {name} must be valid Unicode"),
        )),
    }
}

#[cfg(test)]
mod tests {
    use std::{
        io::{Cursor, Write},
        sync::atomic::AtomicBool,
        sync::Arc,
        time::Duration,
    };

    use serde_json::{json, Value};
    use tokio::sync::Semaphore;
    use zip::{read::ZipArchive, write::FileOptions, ZipWriter};

    use super::{validate_archive, ArchiveError, PackLimits, MANIFEST_SCHEMA, PNG_SIGNATURE};

    fn manifest() -> Value {
        json!({
            "format_version": 1,
            "name": "Test pack",
            "minecraft_version": "1.21.4",
            "loader": "fabric",
            "profile": {"nested": {"unknown_client_field": [1, true, "kept"]}},
            "modrinth_files": [{
                "project_id": "fabric-api",
                "version_id": "test-version",
                "path": "mods/fabric-api.jar",
                "size": 12,
                "sha512": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "enabled": true
            }]
        })
    }

    fn archive_with(manifest: Value, entries: &[(&str, &[u8], Option<u32>)]) -> Vec<u8> {
        let cursor = Cursor::new(Vec::new());
        let mut writer = ZipWriter::new(cursor);
        writer
            .start_file("manifest.json", FileOptions::default())
            .expect("start manifest");
        writer
            .write_all(&serde_json::to_vec(&manifest).expect("serialize manifest"))
            .expect("write manifest");
        for (name, bytes, mode) in entries {
            let options = mode.map_or_else(FileOptions::default, |mode| {
                FileOptions::default().unix_permissions(mode)
            });
            writer.start_file(*name, options).expect("start entry");
            writer.write_all(bytes).expect("write entry");
        }
        let mut bytes = writer.finish().expect("finish ZIP").into_inner();
        for (name, _, mode) in entries {
            if mode.is_some_and(|mode| mode & 0o170000 == 0o120000) {
                mark_symlink(&mut bytes, name);
            }
        }
        bytes
    }

    fn mark_symlink(bytes: &mut [u8], name: &str) {
        let name = name.as_bytes();
        for index in 0..bytes.len().saturating_sub(46) {
            if bytes[index..index + 4] == [0x50, 0x4b, 0x01, 0x02] {
                let name_len = u16::from_le_bytes([bytes[index + 28], bytes[index + 29]]) as usize;
                if name_len == name.len() && &bytes[index + 46..index + 46 + name_len] == name {
                    bytes[index + 5] = 3;
                    bytes[index + 38..index + 42]
                        .copy_from_slice(&(0o120777u32 << 16).to_le_bytes());
                    return;
                }
            }
        }
        panic!("central directory entry {name:?} not found");
    }

    fn duplicate_path(mut bytes: Vec<u8>, original: &[u8], replacement: &[u8]) -> Vec<u8> {
        assert_eq!(original.len(), replacement.len());
        let offsets = bytes
            .windows(original.len())
            .enumerate()
            .filter_map(|(index, window)| (window == original).then_some(index))
            .collect::<Vec<_>>();
        assert!(
            offsets.len() >= 2,
            "both local and central names should be present"
        );
        for offset in offsets {
            bytes[offset..offset + replacement.len()].copy_from_slice(replacement);
        }
        bytes
    }

    fn replace_local_name(bytes: &mut [u8], original: &[u8], replacement: &[u8]) {
        assert_eq!(original.len(), replacement.len());
        for index in 0..bytes.len().saturating_sub(30) {
            if bytes[index..index + 4] == [0x50, 0x4b, 0x03, 0x04] {
                let name_len = u16::from_le_bytes([bytes[index + 26], bytes[index + 27]]) as usize;
                if name_len == original.len()
                    && &bytes[index + 30..index + 30 + name_len] == original
                {
                    bytes[index + 30..index + 30 + name_len].copy_from_slice(replacement);
                    return;
                }
            }
        }
        panic!("local entry name {original:?} not found");
    }

    fn replace_local_method(bytes: &mut [u8], name: &[u8], method: u16) {
        for index in 0..bytes.len().saturating_sub(30) {
            if bytes[index..index + 4] == [0x50, 0x4b, 0x03, 0x04] {
                let name_len = u16::from_le_bytes([bytes[index + 26], bytes[index + 27]]) as usize;
                if name_len == name.len() && &bytes[index + 30..index + 30 + name_len] == name {
                    bytes[index + 8..index + 10].copy_from_slice(&method.to_le_bytes());
                    return;
                }
            }
        }
        panic!("local entry name {name:?} not found");
    }

    fn forge_central_uncompressed_size(bytes: &mut [u8], name: &[u8], size: u32) {
        for index in 0..bytes.len().saturating_sub(46) {
            if bytes[index..index + 4] == [0x50, 0x4b, 0x01, 0x02] {
                let name_len = u16::from_le_bytes([bytes[index + 28], bytes[index + 29]]) as usize;
                if name_len == name.len() && &bytes[index + 46..index + 46 + name_len] == name {
                    bytes[index + 24..index + 28].copy_from_slice(&size.to_le_bytes());
                    return;
                }
            }
        }
        panic!("central entry name {name:?} not found");
    }

    fn validate_bytes(bytes: &[u8], limits: &PackLimits) -> Result<(), ArchiveError> {
        let mut file = tempfile::NamedTempFile::new().expect("create temp archive");
        file.write_all(bytes).expect("write temp archive");
        validate_archive(file.path(), limits, &AtomicBool::new(false)).map(|_| ())
    }

    #[test]
    fn manifest_schema_accepts_full_profile_and_fabric_api_entry() {
        let schema: Value = serde_json::from_str(MANIFEST_SCHEMA).expect("parse schema");
        let validator = jsonschema::validator_for(&schema).expect("compile schema");
        assert!(validator.is_valid(&manifest()));

        let archive = archive_with(manifest(), &[("overrides/options.txt", b"ok", None)]);
        assert!(validate_bytes(&archive, &PackLimits::default()).is_ok());
    }

    #[test]
    fn schema_rejects_invalid_manifest_fields() {
        let schema: Value = serde_json::from_str(MANIFEST_SCHEMA).expect("parse schema");
        let validator = jsonschema::validator_for(&schema).expect("compile schema");
        let mut invalid = manifest();
        invalid["format_version"] = json!(2);
        assert!(!validator.is_valid(&invalid));
    }

    #[test]
    fn rejects_encrypted_entries() {
        let mut bytes = archive_with(manifest(), &[("overrides/options.txt", b"ok", None)]);
        for index in 0..bytes.len().saturating_sub(4) {
            if bytes[index..index + 4] == [0x50, 0x4b, 0x03, 0x04] {
                bytes[index + 6] |= 1;
            } else if bytes[index..index + 4] == [0x50, 0x4b, 0x01, 0x02] {
                bytes[index + 8] |= 1;
            }
        }
        assert_eq!(
            validate_bytes(&bytes, &PackLimits::default()),
            Err(ArchiveError::Invalid)
        );

        let mut local_only = archive_with(manifest(), &[("overrides/options.txt", b"ok", None)]);
        for index in 0..local_only.len().saturating_sub(4) {
            if local_only[index..index + 4] == [0x50, 0x4b, 0x03, 0x04] {
                local_only[index + 6] |= 1;
            }
        }
        assert_eq!(
            validate_bytes(&local_only, &PackLimits::default()),
            Err(ArchiveError::Invalid)
        );
    }

    #[test]
    fn rejects_symlinks_and_unsafe_paths() {
        let symlink = archive_with(manifest(), &[("overrides/link", b"target", Some(0o120777))]);
        assert_eq!(
            validate_bytes(&symlink, &PackLimits::default()),
            Err(ArchiveError::Invalid)
        );

        for path in [
            "/etc/passwd",
            "C:/outside.txt",
            "../outside.txt",
            "overrides/../outside.txt",
            "outside/",
            "overrides/options.txt:payload",
            "overrides/trailing.",
            "overrides/trailing ",
            "overrides/CON",
            "overrides/con.txt",
            "overrides/PRN.log",
            "overrides/AUX.png",
            "overrides/nul",
            "overrides/COM1.jar",
            "overrides/com2.jar",
            "overrides/COM3.jar",
            "overrides/COM4.jar",
            "overrides/COM5.jar",
            "overrides/COM6.jar",
            "overrides/COM7.jar",
            "overrides/COM8.jar",
            "overrides/COM9.jar",
            "overrides/COM¹.txt",
            "overrides/LPT²",
            "overrides/nested/com³",
            "overrides/LPT1.txt",
            "overrides/lpt2.txt",
            "overrides/LPT3.txt",
            "overrides/LPT4.txt",
            "overrides/LPT5.txt",
            "overrides/LPT6.txt",
            "overrides/LPT7.txt",
            "overrides/LPT8.txt",
            "overrides/LPT9.txt",
        ] {
            let archive = archive_with(manifest(), &[(path, b"x", None)]);
            assert_eq!(
                validate_bytes(&archive, &PackLimits::default()),
                Err(ArchiveError::Invalid),
                "path {path} should be rejected"
            );
        }
    }

    #[test]
    fn rejects_exact_case_insensitive_and_disabled_collisions() {
        let exact = archive_with(
            manifest(),
            &[
                ("overrides/a.txt", b"one", None),
                ("overrides/b.txt", b"two", None),
            ],
        );
        let exact = duplicate_path(exact, b"overrides/b.txt", b"overrides/a.txt");
        let mut zip = ZipArchive::new(Cursor::new(exact.as_slice())).expect("read source ZIP");
        let names = (0..zip.len())
            .map(|index| {
                zip.by_index(index)
                    .expect("read ZIP entry")
                    .name()
                    .to_owned()
            })
            .collect::<Vec<_>>();
        assert_eq!(names.len(), 3, "unexpected source ZIP entries: {names:?}");
        assert_eq!(
            names[1], names[2],
            "archive entries were not duplicated: {names:?}"
        );
        drop(zip);
        assert_eq!(
            validate_bytes(&exact, &PackLimits::default()),
            Err(ArchiveError::Invalid)
        );

        let cases: &[(&str, &str)] = &[
            ("overrides/Mods/a.jar", "overrides/mods/A.jar"),
            ("overrides/mods/a.jar", "overrides/mods/a.jar.disabled"),
        ];
        for (first, second) in cases {
            let archive =
                archive_with(manifest(), &[(first, b"one", None), (second, b"two", None)]);
            assert_eq!(
                validate_bytes(&archive, &PackLimits::default()),
                Err(ArchiveError::Invalid),
                "collision {first} / {second} should be rejected"
            );
        }
    }

    #[test]
    fn enforces_entry_manifest_and_uncompressed_limits() {
        let archive = archive_with(manifest(), &[("overrides/file", &[7; 512], None)]);
        let defaults = PackLimits::default();
        assert_eq!(
            validate_bytes(
                &archive,
                &PackLimits {
                    max_entries: 1,
                    ..defaults.clone()
                }
            ),
            Err(ArchiveError::TooLarge)
        );
        assert_eq!(
            validate_bytes(
                &archive,
                &PackLimits {
                    max_manifest_bytes: 16,
                    ..defaults.clone()
                }
            ),
            Err(ArchiveError::TooLarge)
        );
        assert_eq!(
            validate_bytes(
                &archive,
                &PackLimits {
                    max_uncompressed_bytes: 256,
                    ..defaults
                }
            ),
            Err(ArchiveError::TooLarge)
        );
    }

    #[test]
    fn rejects_archive_when_validation_budget_is_exhausted() {
        let archive = archive_with(manifest(), &[("overrides/options.txt", b"ok", None)]);
        let limits = PackLimits {
            validation_timeout: Duration::ZERO,
            ..PackLimits::default()
        };

        assert_eq!(
            validate_bytes(&archive, &limits),
            Err(ArchiveError::TimedOut)
        );
    }

    #[tokio::test]
    async fn validation_budget_exhaustion_returns_invalid_archive() {
        use axum::{body::Body, http::StatusCode, response::IntoResponse};

        let archive = archive_with(manifest(), &[("overrides/options.txt", b"ok", None)]);
        let limits = PackLimits {
            validation_timeout: Duration::ZERO,
            ..PackLimits::default()
        };
        let error = match super::receive_archive(Body::from(archive), None, &limits).await {
            Err(error) => error,
            Ok(_) => panic!("validation past its budget should reject the archive"),
        };

        assert_eq!(error.into_response().status(), StatusCode::BAD_REQUEST);
    }

    #[test]
    fn checks_actual_expansion_when_central_size_is_forged_small() {
        let payload = vec![b'x'; 8192];
        let mut archive = archive_with(
            manifest(),
            &[("overrides/payload", payload.as_slice(), None)],
        );
        forge_central_uncompressed_size(&mut archive, b"overrides/payload", 1);

        assert_eq!(
            validate_bytes(
                &archive,
                &PackLimits {
                    max_uncompressed_bytes: 1024,
                    ..PackLimits::default()
                }
            ),
            Err(ArchiveError::TooLarge)
        );
        assert_eq!(
            validate_bytes(&archive, &PackLimits::default()),
            Err(ArchiveError::Invalid)
        );
    }

    #[test]
    fn rejects_local_header_name_and_method_mismatches() {
        let mut renamed = archive_with(manifest(), &[("overrides/x", b"ok", None)]);
        replace_local_name(&mut renamed, b"overrides/x", b"../evil.txt");
        assert_eq!(
            validate_bytes(&renamed, &PackLimits::default()),
            Err(ArchiveError::Invalid)
        );

        let mut method_changed = archive_with(manifest(), &[("overrides/x", b"ok", None)]);
        replace_local_method(&mut method_changed, b"overrides/x", 99);
        assert_eq!(
            validate_bytes(&method_changed, &PackLimits::default()),
            Err(ArchiveError::Invalid)
        );
    }

    #[test]
    fn checks_optional_icon_png_magic_bytes() {
        let mut with_icon = manifest();
        with_icon["icon"] = json!("icon.png");
        let invalid = archive_with(with_icon.clone(), &[("icon.png", b"not a PNG", None)]);
        assert_eq!(
            validate_bytes(&invalid, &PackLimits::default()),
            Err(ArchiveError::Invalid)
        );
        let valid = archive_with(with_icon, &[("icon.png", PNG_SIGNATURE, None)]);
        assert!(validate_bytes(&valid, &PackLimits::default()).is_ok());
    }

    #[tokio::test]
    async fn streamed_upload_stops_when_archive_limit_is_exceeded() {
        use axum::{body::Body, http::StatusCode, response::IntoResponse};

        let limits = PackLimits {
            max_archive_bytes: 8,
            ..PackLimits::default()
        };
        let error = match super::receive_archive(Body::from(vec![0u8; 9]), None, &limits).await {
            Err(error) => error,
            Ok(_) => panic!("oversized upload should stop during streaming"),
        };
        assert_eq!(
            error.into_response().status(),
            StatusCode::PAYLOAD_TOO_LARGE
        );
    }

    #[tokio::test]
    async fn rejects_archive_when_validation_capacity_is_full() {
        use axum::{body::Body, http::StatusCode, response::IntoResponse};

        let limits = PackLimits {
            validation_semaphore: Arc::new(Semaphore::new(1)),
            ..PackLimits::default()
        };
        let _active_validation = Arc::clone(&limits.validation_semaphore)
            .acquire_owned()
            .await
            .expect("hold validation capacity for active request");
        let result = tokio::time::timeout(
            Duration::from_secs(2),
            super::receive_archive(Body::empty(), None, &limits),
        )
        .await
        .expect("queued request must return instead of waiting indefinitely");
        let error = match result {
            Err(error) => error,
            Ok(_) => panic!("request should be rejected while validation is full"),
        };
        let response = error.into_response();

        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
        assert_eq!(response.headers().get("retry-after").unwrap(), "1");
    }

    #[tokio::test]
    async fn releases_validation_slot_after_active_request_is_cancelled() {
        use axum::body::Body;

        let mut writer = ZipWriter::new(Cursor::new(Vec::new()));
        writer
            .start_file("manifest.json", FileOptions::default())
            .expect("start manifest");
        writer
            .write_all(&serde_json::to_vec(&manifest()).expect("serialize manifest"))
            .expect("write manifest");
        for index in 0..9_999 {
            writer
                .start_file(format!("overrides/{index}.txt"), FileOptions::default())
                .expect("start archive entry");
        }
        let bytes = writer.finish().expect("finish archive").into_inner();
        let limits = PackLimits {
            validation_semaphore: Arc::new(Semaphore::new(1)),
            ..PackLimits::default()
        };
        let semaphore = Arc::clone(&limits.validation_semaphore);
        let validation_limits = limits.clone();
        let validation = tokio::spawn(async move {
            super::receive_archive(Body::from(bytes), None, &validation_limits).await
        });

        tokio::time::timeout(Duration::from_secs(5), async {
            while semaphore.available_permits() != 0 {
                tokio::task::yield_now().await;
            }
        })
        .await
        .expect("request should acquire a slot and enter archive validation");
        assert!(
            !validation.is_finished(),
            "validation should still be active"
        );
        validation.abort();
        let _ = validation.await;

        tokio::time::timeout(Duration::from_secs(5), async {
            while semaphore.available_permits() != 1 {
                tokio::task::yield_now().await;
            }
        })
        .await
        .expect("cancelled validation should observe cancellation and release its slot");
    }
}
