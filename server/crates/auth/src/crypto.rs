use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;

use crate::error::AuthError;

pub fn random_secret(byte_count: usize) -> Result<(String, Vec<u8>), AuthError> {
    let mut bytes = vec![0; byte_count];
    getrandom::fill(&mut bytes).map_err(|_| AuthError::internal())?;
    let encoded = URL_SAFE_NO_PAD.encode(&bytes);
    let hash = hash(encoded.as_bytes());
    Ok((encoded, hash))
}

pub fn hash(value: &[u8]) -> Vec<u8> {
    Sha256::digest(value).to_vec()
}

pub fn verify_secret(candidate: &str, expected_secret: &str) -> bool {
    let candidate_hash = Sha256::digest(candidate.as_bytes());
    let expected_hash = Sha256::digest(expected_secret.as_bytes());
    bool::from(candidate_hash.as_slice().ct_eq(expected_hash.as_slice()))
}

pub fn validate_device_name(value: &str) -> bool {
    let length = value.chars().count();
    (1..=128).contains(&length) && !value.trim().is_empty()
}

pub fn validate_username(value: &str) -> bool {
    (5..=32).contains(&value.len())
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
}

pub fn bot_link(username: &str, code: &str) -> String {
    format!("https://t.me/{username}?start={code}")
}

#[cfg(test)]
mod tests {
    use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};

    use super::{
        bot_link, hash, random_secret, validate_device_name, validate_username, verify_secret,
    };

    #[test]
    fn secrets_are_urlsafe_and_hashed_without_returning_the_hash_as_the_secret() {
        let (secret, digest) = random_secret(32).expect("random secret");
        let decoded = URL_SAFE_NO_PAD.decode(&secret).expect("urlsafe secret");

        assert_eq!(decoded.len(), 32);
        assert_eq!(digest, hash(secret.as_bytes()));
        assert_ne!(secret.as_bytes(), digest.as_slice());
        assert!(secret
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_' || byte == b'-'));
    }

    #[test]
    fn compares_secrets_and_builds_bot_deep_links() {
        assert!(verify_secret("same secret", "same secret"));
        assert!(!verify_secret("different", "same secret"));
        assert_eq!(
            bot_link("hako_bot", "code_123"),
            "https://t.me/hako_bot?start=code_123"
        );
    }

    #[test]
    fn validates_telegram_names_and_device_names() {
        assert!(validate_username("Player_123"));
        assert!(!validate_username("abcd"));
        assert!(!validate_username("bad-name"));
        assert!(!validate_username("имя_пользователя"));
        assert!(validate_device_name("Gaming PC"));
        assert!(validate_device_name(&"a".repeat(128)));
        assert!(!validate_device_name(" "));
        assert!(!validate_device_name(&"a".repeat(129)));
    }
}
