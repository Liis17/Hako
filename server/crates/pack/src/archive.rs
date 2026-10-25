use std::{
    collections::HashSet,
    env,
    fs::File,
    io::{self, ErrorKind, Read, Seek, SeekFrom},
    path::Path,
    time::Duration,
};

use axum::body::Body;
use futures_util::StreamExt;
use serde_json::Value;
use sha2::{Digest, Sha256};
use tempfile::NamedTempFile;
use tokio::io::AsyncWriteExt;
use zip::ZipArchive;

use crate::error::{PackError, PackResult};

pub const MAX_DOWNLOAD_DURATION: Duration = Duration::from_secs(60 * 60);
const DEFAULT_MAX_ARCHIVE_BYTES: u64 = 1_073_741_824;
const DEFAULT_MAX_UNCOMPRESSED_BYTES: u64 = 4_294_967_296;
const DEFAULT_MAX_ENTRIES: usize = 10_000;
const DEFAULT_MAX_MANIFEST_BYTES: u64 = 1_048_576;
const MANIFEST_SCHEMA: &str = include_str!("../../../docs/hakopack-manifest.schema.json");
const PNG_SIGNATURE: &[u8; 8] = b"\x89PNG\r\n\x1a\n";

#[derive(Clone, Debug)]
pub struct PackLimits {
    pub max_archive_bytes: u64,
    pub max_uncompressed_bytes: u64,
    pub max_entries: usize,
    pub max_manifest_bytes: u64,
}

impl Default for PackLimits {
    fn default() -> Self {
        Self {
            max_archive_bytes: DEFAULT_MAX_ARCHIVE_BYTES,
            max_uncompressed_bytes: DEFAULT_MAX_UNCOMPRESSED_BYTES,
            max_entries: DEFAULT_MAX_ENTRIES,
            max_manifest_bytes: DEFAULT_MAX_MANIFEST_BYTES,
        }
    }
}

impl PackLimits {
    pub fn from_env() -> Result<Self, io::Error> {
        let defaults = Self::default();
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

    let archive_path = temp.path().to_path_buf();
    let validation_limits = limits.clone();
    let manifest =
        tokio::task::spawn_blocking(move || validate_archive(&archive_path, &validation_limits))
            .await
            .map_err(|_| PackError::internal())?
            .map_err(|error| match error {
                ArchiveError::TooLarge => PackError::payload_too_large(),
                ArchiveError::Invalid => PackError::invalid_archive(),
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

fn validate_archive(path: &Path, limits: &PackLimits) -> Result<ManifestMetadata, ArchiveError> {
    let mut file = File::open(path).map_err(|_| ArchiveError::Invalid)?;
    if declared_entry_count(&mut file)? > limits.max_entries {
        return Err(ArchiveError::TooLarge);
    }
    let mut archive = ZipArchive::new(file).map_err(|_| ArchiveError::Invalid)?;
    let mut header_reader = File::open(path).map_err(|_| ArchiveError::Invalid)?;
    if archive.len() > limits.max_entries {
        return Err(ArchiveError::TooLarge);
    }

    let mut paths = HashSet::with_capacity(archive.len());
    let mut uncompressed_bytes = 0u128;
    let mut manifest_bytes = None;
    let mut icon_is_png = None;

    for index in 0..archive.len() {
        let mut entry = archive.by_index(index).map_err(|_| ArchiveError::Invalid)?;
        let name = entry.name().to_owned();
        let local_header = entry.header_start();
        let central_header = entry.central_header_start();
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

        uncompressed_bytes = uncompressed_bytes
            .checked_add(u128::from(entry.size()))
            .ok_or(ArchiveError::TooLarge)?;
        if uncompressed_bytes > u128::from(limits.max_uncompressed_bytes) {
            return Err(ArchiveError::TooLarge);
        }

        if name == "manifest.json" && !entry.is_dir() {
            if entry.size() > limits.max_manifest_bytes {
                return Err(ArchiveError::TooLarge);
            }
            let mut bytes = Vec::with_capacity(entry.size() as usize);
            entry
                .take(limits.max_manifest_bytes.saturating_add(1))
                .read_to_end(&mut bytes)
                .map_err(|_| ArchiveError::Invalid)?;
            if bytes.len() as u64 > limits.max_manifest_bytes {
                return Err(ArchiveError::TooLarge);
            }
            manifest_bytes = Some(bytes);
        } else if name == "icon.png" && !entry.is_dir() {
            let mut signature = [0u8; 8];
            let is_png = entry.read_exact(&mut signature).is_ok() && &signature == PNG_SIGNATURE;
            icon_is_png = Some(is_png);
        } else if !name.starts_with("overrides/") {
            return Err(ArchiveError::Invalid);
        }
    }

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

fn validate_path(name: &str) -> Result<(), ()> {
    if name.is_empty()
        || name.contains(['\\', '\0'])
        || name.starts_with('/')
        || name.as_bytes().get(1) == Some(&b':')
    {
        return Err(());
    }
    let path = name.strip_suffix('/').unwrap_or(name);
    if path.is_empty()
        || path
            .split('/')
            .any(|segment| segment.is_empty() || segment == "." || segment == "..")
    {
        return Err(());
    }
    Ok(())
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
    use std::io::{Cursor, Write};

    use serde_json::{json, Value};
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

    fn validate_bytes(bytes: &[u8], limits: &PackLimits) -> Result<(), ArchiveError> {
        let mut file = tempfile::NamedTempFile::new().expect("create temp archive");
        file.write_all(bytes).expect("write temp archive");
        validate_archive(file.path(), limits).map(|_| ())
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
}
