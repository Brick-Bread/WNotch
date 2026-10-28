//! Signatures on installers. Ported from `src/Notch.Core/Updates/UpdateSigning.cs`.
//!
//! An update is trusted because the maintainer signed it and not only because it came from
//! GitHub. ECDSA with P-256 and SHA-256, the same as the C# app uses through .NET. A signature is
//! the base64 of the 64-byte (r, s) pair (IEEE P1363), kept in a `.sig` file beside the installer.
//! Keys are base64 too: the public key a SubjectPublicKeyInfo, the private key PKCS#8.

use base64::Engine;
use base64::engine::{DecodePaddingMode, GeneralPurpose, GeneralPurposeConfig};
use p256::ecdsa::signature::{Signer, Verifier};
use p256::ecdsa::{Signature, SigningKey, VerifyingKey};
use p256::pkcs8::{DecodePrivateKey, DecodePublicKey, EncodePrivateKey, EncodePublicKey};

/// The key official installers are signed with, as the base64 of an X.509 SubjectPublicKeyInfo.
/// Empty until the maintainer makes a key pair with `installer/new-update-key.ps1` and pastes the
/// public half here; while it is empty, updates are checked against the size and digest GitHub
/// lists for them, as before. Once it is set, an installer without a valid signature is never run.
pub const PUBLIC_KEY: &str = "";

/// Whether `public_key` (normally [`PUBLIC_KEY`]) is set.
pub fn is_configured(public_key: Option<&str>) -> bool {
    public_key.is_some_and(|key| !key.trim().is_empty())
}

/// A new key pair: the public key to embed in the app and the private key to keep as a build secret.
pub fn new_key_pair() -> (String, String) {
    let key = loop {
        let mut bytes = [0u8; 32];
        getrandom::fill(&mut bytes).expect("the system has no source of randomness");
        // All but a vanishing share of 32-byte strings are valid P-256 private keys.
        if let Ok(key) = SigningKey::from_slice(&bytes) {
            break key;
        }
    };

    // Encoding a valid key cannot fail.
    let public = key.verifying_key().to_public_key_der().expect("a P-256 public key encodes");
    let private = key.to_pkcs8_der().expect("a P-256 private key encodes");
    (BASE64.encode(public.as_bytes()), BASE64.encode(private.as_bytes()))
}

/// Signs a file's contents. Used by the build, never by the app. `None` when the private key
/// cannot be read, where C# throws.
pub fn sign(data: &[u8], private_key: &str) -> Option<String> {
    let key = SigningKey::from_pkcs8_der(&from_base64(private_key.trim())?).ok()?;
    let signature: Signature = key.sign(data);
    Some(BASE64.encode(signature.to_bytes()))
}

/// Whether `signature` is the public key's holder's signature of `data`. False for anything malformed.
pub fn verify(data: &[u8], signature: Option<&str>, public_key: &str) -> bool {
    let Some(signature) = signature.filter(|signature| !signature.trim().is_empty()) else {
        return false;
    };

    let Some(bytes) = from_base64(signature.trim()).filter(|bytes| bytes.len() == 64) else {
        return false;
    };

    let Some(key) = from_base64(public_key.trim()).and_then(|der| VerifyingKey::from_public_key_der(&der).ok()) else {
        return false;
    };

    Signature::from_slice(&bytes).is_ok_and(|signature| key.verify(data, &signature).is_ok())
}

/// Standard base64 with padding, written by `Convert.ToBase64String`.
const BASE64: GeneralPurpose = base64::engine::general_purpose::STANDARD;

/// `Convert.FromBase64String`: padding is required, spaces, tabs and line breaks anywhere are
/// skipped, and unused bits in the last character are not checked.
fn from_base64(text: &str) -> Option<Vec<u8>> {
    const LENIENT: GeneralPurpose = GeneralPurpose::new(
        &base64::alphabet::STANDARD,
        GeneralPurposeConfig::new()
            .with_decode_allow_trailing_bits(true)
            .with_decode_padding_mode(DecodePaddingMode::RequireCanonical),
    );
    let compact: String = text.chars().filter(|c| !matches!(c, ' ' | '\t' | '\r' | '\n')).collect();
    LENIENT.decode(compact).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    const INSTALLER: &[u8] = b"pretend this is an installer";

    #[test]
    fn a_signature_verifies_only_for_the_file_and_key_it_was_made_with() {
        let (public_key, private_key) = new_key_pair();
        let (other_public, _) = new_key_pair();
        let changed = [INSTALLER, &[0]].concat();

        let signature = sign(INSTALLER, &private_key).unwrap();

        assert!(verify(INSTALLER, Some(&signature), &public_key));
        assert!(!verify(&changed, Some(&signature), &public_key));
        assert!(!verify(INSTALLER, Some(&signature), &other_public));
    }

    #[test]
    fn malformed_signatures_are_simply_invalid() {
        let (public_key, _) = new_key_pair();
        for signature in [None, Some(""), Some("not base64!"), Some("AAAA")] {
            assert!(!verify(INSTALLER, signature, &public_key), "{signature:?}");
        }
    }

    #[test]
    fn a_broken_public_key_never_verifies() {
        assert!(!verify(INSTALLER, Some(&BASE64.encode([0u8; 64])), "nonsense"));
    }

    #[test]
    fn the_embedded_key_is_empty_until_the_maintainer_sets_one() {
        // This documents the state of the repository: flip it when installer/new-update-key.ps1 has been run.
        assert_eq!(!PUBLIC_KEY.is_empty(), is_configured(Some(PUBLIC_KEY)));
    }

    #[test]
    fn base64_is_read_as_dotnet_reads_it() {
        assert_eq!(from_base64("AA\r\nA= "), Some(vec![0, 0]));
        assert_eq!(from_base64("AAB="), Some(vec![0, 0]));
        assert_eq!(from_base64("AAA"), None);
        assert_eq!(from_base64("A-_A"), None);
    }

    #[test]
    fn signatures_and_keys_tolerate_surrounding_white_space() {
        let (public_key, private_key) = new_key_pair();
        let signature = sign(INSTALLER, &format!("{private_key}\n")).unwrap();
        assert!(verify(INSTALLER, Some(&format!(" {signature}\r\n")), &format!("{public_key}\n")));
        assert_eq!(sign(INSTALLER, "nonsense"), None);
    }
}
