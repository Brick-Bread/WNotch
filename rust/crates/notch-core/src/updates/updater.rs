//! Finds newer releases and checks each downloaded installer before it is trusted. Ported from
//! `src/Notch.Core/Updates/Updater.cs`.
//!
//! The HTTP stays with the caller; this holds the requests to make and the checks on what comes
//! back. As C# does it:
//! 1. Check: GET [`LATEST_RELEASE_URL`] with `User-Agent: Notch-Updater` and
//!    `Accept: application/vnd.github+json`. A status other than success is an error ("GitHub could
//!    not be reached"). Pass the body to [`check`].
//! 2. Download: GET the release's installer URL with the same user agent into
//!    [`partial_file_name`] in the download folder (created if missing).
//! 3. If [`signature_to_fetch`] names a URL, GET it with the same user agent. A success status is
//!    required, and a `Content-Length` [`check_signature_size`] refuses ends it there. Read the
//!    body as text.
//! 4. [`verify`] the installer bytes with that signature, then rename the partial file to
//!    [`installer_file_name`], replacing one already there. On any failure, delete the partial file.

use sha2::{Digest, Sha256};

use super::feed::{self, ReleaseInfo, Version};
use super::signing;
use crate::automation::parser::utf16_len;

pub use super::feed::LATEST_RELEASE_URL;

/// GitHub rejects API requests without a user agent.
pub const USER_AGENT: &str = "Notch-Updater";
pub const ACCEPT: &str = "application/vnd.github+json";
pub const MAX_SIGNATURE_LENGTH: usize = 1024;

/// The latest release in `feed` (the body of [`LATEST_RELEASE_URL`]) if it is newer than
/// `current`, otherwise `None`.
pub fn check(feed: &str, current: Version) -> Option<ReleaseInfo> {
    feed::parse(feed).filter(|release| feed::is_newer(release, current))
}

/// Where a downloaded installer is kept.
pub fn installer_file_name(release: &ReleaseInfo) -> String {
    format!("Notch-Setup-{}.exe", release.version)
}

/// Where the installer is written while it downloads and until it is verified.
pub fn partial_file_name(release: &ReleaseInfo) -> String {
    format!("{}.part", installer_file_name(release))
}

/// The installer's size, which must not be 0 and must match what the release lists when it lists one.
pub fn verify_size(release: &ReleaseInfo, size: u64) -> Result<(), String> {
    if size == 0 || (release.installer_size > 0 && i64::try_from(size) != Ok(release.installer_size)) {
        return Err(format!("The installer is {size} bytes; the release lists {}.", release.installer_size));
    }
    Ok(())
}

/// The signature to download for `release`, when `signing_key` (normally
/// [`signing::PUBLIC_KEY`]) is set. With a key, an installer must be signed.
pub fn signature_to_fetch<'a>(release: &'a ReleaseInfo, signing_key: Option<&str>) -> Result<Option<&'a str>, String> {
    if !signing::is_configured(signing_key) {
        return Ok(None);
    }
    release.signature_url.as_deref().map(Some).ok_or_else(|| "The installer is not signed.".to_owned())
}

/// Refuses a signature response whose `Content-Length` is too large to be a signature.
pub fn check_signature_size(content_length: Option<u64>) -> Result<(), String> {
    if content_length.is_some_and(|length| length > MAX_SIGNATURE_LENGTH as u64) {
        return Err("The installer's signature is not a signature.".to_owned());
    }
    Ok(())
}

/// Checks a downloaded installer: its size, then (with a signing key) the maintainer's signature,
/// then the digest GitHub lists. `signature` is the text of the `.sig` file, when one was fetched.
pub fn verify(
    release: &ReleaseInfo,
    installer: &[u8],
    signature: Option<&str>,
    signing_key: Option<&str>,
) -> Result<(), String> {
    verify_size(release, installer.len() as u64)?;

    let signed = signing::is_configured(signing_key);
    if signed {
        signature_to_fetch(release, signing_key)?;
        let key = signing_key.unwrap_or_default();
        let signature = signature.unwrap_or_default();
        if utf16_len(signature) > MAX_SIGNATURE_LENGTH || !signing::verify(installer, Some(signature), key) {
            return Err("The installer's signature is not valid.".to_owned());
        }
    } else if release.sha256.is_none() {
        // Nothing to check it against: an installer that cannot be verified is not run.
        return Err(
            "The release lists no digest for the installer and is not signed, so it cannot be verified.".to_owned()
        );
    }

    let Some(expected) = &release.sha256 else {
        return Ok(());
    };

    let actual: String = Sha256::digest(installer).iter().map(|byte| format!("{byte:02x}")).collect();
    if !actual.eq_ignore_ascii_case(expected) {
        return Err("The installer does not match the digest the release lists for it.".to_owned());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const DOWNLOAD: &str = "https://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe";
    const INSTALLER: &[u8] = b"pretend this is an installer";

    fn release_json(tag: &str, url: &str, size: Option<i64>, digest: Option<&str>) -> String {
        let size = size.unwrap_or(INSTALLER.len() as i64);
        let digest = digest.map_or("null".to_owned(), |digest| format!("\"{digest}\""));
        format!(
            r#"{{
              "tag_name": "{tag}",
              "assets": [
                {{ "name": "notes.txt", "browser_download_url": "https://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/notes.txt", "size": 3 }},
                {{ "name": "Notch-Setup-0.2.0.exe", "browser_download_url": "{url}", "size": {size}, "digest": {digest} }}
              ]
            }}"#
        )
    }

    fn default_json() -> String {
        release_json("v0.2.0", DOWNLOAD, None, None)
    }

    fn sha256(bytes: &[u8]) -> String {
        format!("sha256:{}", Sha256::digest(bytes).iter().map(|byte| format!("{byte:02x}")).collect::<String>())
    }

    #[test]
    fn tags_parse_to_versions() {
        for (tag, expected) in [("v1.2.3", "1.2.3"), ("1.2", "1.2.0"), ("v2.0.0-beta.1", "2.0.0")] {
            assert_eq!(feed::try_parse_version(tag), Version::parse(expected), "{tag}");
            assert_eq!(feed::try_parse_version(tag).unwrap().to_string(), expected);
        }
    }

    #[test]
    fn nonsense_tags_are_rejected() {
        assert_eq!(feed::try_parse_version("nightly"), None);
    }

    #[test]
    fn release_prefers_the_update_copy_so_updates_do_not_count_as_downloads() {
        let update = "https://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/Notch-Update-0.2.0.exe";
        let json = default_json().replace(
            "\"assets\": [",
            &format!("\"assets\": [ {{ \"name\": \"Notch-Update-0.2.0.exe\", \"browser_download_url\": \"{update}\", \"size\": 5 }},"),
        );

        assert_eq!(feed::parse(&json).unwrap().installer_url, update);
    }

    #[test]
    fn release_picks_the_installer_asset() {
        let release = feed::parse(&release_json("v0.2.0", DOWNLOAD, None, Some("sha256:ABCDEF"))).unwrap();

        assert_eq!(release.version, Version::new(0, 2, 0));
        assert_eq!(release.installer_url, DOWNLOAD);
        assert_eq!(release.installer_size, INSTALLER.len() as i64);
        assert_eq!(release.sha256.as_deref(), Some("abcdef"));
    }

    #[test]
    fn installers_from_anywhere_else_are_ignored() {
        for url in [
            "https://example.com/Notch-Setup-0.2.0.exe",
            "https://github.com/someone-else/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe",
            "http://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe",
        ] {
            assert_eq!(feed::parse(&release_json("v0.2.0", url, None, None)), None, "{url}");
        }
    }

    #[test]
    fn unusable_releases_parse_to_null() {
        for json in ["{}", "not json", r#"{"tag_name":"v1.0.0","assets":[]}"#] {
            assert_eq!(feed::parse(json), None, "{json}");
        }
    }

    #[test]
    fn only_newer_releases_count() {
        let release = feed::parse(&default_json()).unwrap();
        for (current, expected) in [("0.1.0.0", true), ("0.2.0.0", false), ("0.2.0", false), ("0.3.0", false)] {
            assert_eq!(feed::is_newer(&release, Version::parse(current).unwrap()), expected, "{current}");
        }
    }

    #[test]
    fn check_returns_a_release_only_when_it_is_newer() {
        assert!(check(&default_json(), Version::new(0, 1, 0)).is_some());
        assert!(check(&default_json(), Version::new(0, 2, 0)).is_none());
    }

    // The download tests check the bytes the caller downloaded; deleting the partial file on
    // failure is the caller's part.

    #[test]
    fn download_saves_a_verified_installer() {
        let release = feed::parse(&release_json("v0.2.0", DOWNLOAD, None, Some(&sha256(INSTALLER)))).unwrap();

        assert_eq!(verify(&release, INSTALLER, None, Some(signing::PUBLIC_KEY)), Ok(()));
        assert_eq!(installer_file_name(&release), "Notch-Setup-0.2.0.exe");
        assert_eq!(partial_file_name(&release), "Notch-Setup-0.2.0.exe.part");
    }

    #[test]
    fn download_rejects_a_tampered_installer() {
        let release =
            feed::parse(&release_json("v0.2.0", DOWNLOAD, None, Some(&sha256(b"something else entirely!!!!!"))))
                .unwrap();

        assert!(verify(&release, INSTALLER, None, Some(signing::PUBLIC_KEY)).is_err());
    }

    #[test]
    fn download_rejects_a_truncated_installer() {
        let release = feed::parse(&release_json("v0.2.0", DOWNLOAD, Some(INSTALLER.len() as i64 + 100), None)).unwrap();

        assert_eq!(
            verify(&release, INSTALLER, None, Some(signing::PUBLIC_KEY)),
            Err("The installer is 28 bytes; the release lists 128.".to_owned())
        );
    }

    // UpdateSigningTests.

    const INSTALLER_URL: &str = DOWNLOAD;
    const SIGNATURE_URL: &str =
        "https://github.com/Brick-Bread/WNotch/releases/download/v0.2.0/Notch-Setup-0.2.0.exe.sig";

    fn signed_json(with_signature: bool, digest: Option<&str>) -> String {
        let digest = digest.map_or("null".to_owned(), |digest| format!("\"{digest}\""));
        let signature = if with_signature {
            format!(
                r#", {{ "name": "Notch-Setup-0.2.0.exe.sig", "browser_download_url": "{SIGNATURE_URL}", "size": 88 }}"#
            )
        } else {
            String::new()
        };
        format!(
            r#"{{
              "tag_name": "v0.2.0",
              "assets": [
                {{ "name": "Notch-Setup-0.2.0.exe", "browser_download_url": "{INSTALLER_URL}", "size": {}, "digest": {digest} }}
                {signature}
              ]
            }}"#,
            INSTALLER.len()
        )
    }

    #[test]
    fn the_signature_asset_is_found_beside_the_installer() {
        assert_eq!(feed::parse(&signed_json(true, None)).unwrap().signature_url.as_deref(), Some(SIGNATURE_URL));
        assert_eq!(feed::parse(&signed_json(false, None)).unwrap().signature_url, None);
    }

    #[test]
    fn a_signature_hosted_elsewhere_is_ignored() {
        let json = signed_json(true, None).replace(SIGNATURE_URL, "https://example.com/Notch-Setup-0.2.0.exe.sig");
        assert_eq!(feed::parse(&json).unwrap().signature_url, None);
    }

    #[test]
    fn a_signed_installer_is_downloaded() {
        let (public_key, private_key) = signing::new_key_pair();
        let signature = signing::sign(INSTALLER, &private_key).unwrap();
        let release = feed::parse(&signed_json(true, Some(&sha256(INSTALLER)))).unwrap();

        assert_eq!(signature_to_fetch(&release, Some(&public_key)), Ok(Some(SIGNATURE_URL)));
        assert_eq!(verify(&release, INSTALLER, Some(&signature), Some(&public_key)), Ok(()));
    }

    #[test]
    fn with_a_key_an_unsigned_release_is_refused_even_with_a_matching_digest() {
        let (public_key, _) = signing::new_key_pair();
        let release = feed::parse(&signed_json(false, Some(&sha256(INSTALLER)))).unwrap();

        assert_eq!(signature_to_fetch(&release, Some(&public_key)), Err("The installer is not signed.".to_owned()));
        assert!(verify(&release, INSTALLER, Some(""), Some(&public_key)).is_err());
    }

    #[test]
    fn a_signature_from_another_key_is_refused() {
        let (public_key, _) = signing::new_key_pair();
        let (_, attacker_key) = signing::new_key_pair();
        let release = feed::parse(&signed_json(true, Some(&sha256(INSTALLER)))).unwrap();
        let signature = signing::sign(INSTALLER, &attacker_key).unwrap();

        assert_eq!(
            verify(&release, INSTALLER, Some(&signature), Some(&public_key)),
            Err("The installer's signature is not valid.".to_owned())
        );
    }

    #[test]
    fn a_replaced_installer_fails_even_when_github_lists_its_digest() {
        // Someone who can edit the release can also change the digest GitHub shows; they cannot sign.
        let (public_key, private_key) = signing::new_key_pair();
        let evil = b"pretend this is malware....";
        let signature = signing::sign(INSTALLER, &private_key).unwrap();
        let mut release = feed::parse(&signed_json(true, Some(&sha256(evil)))).unwrap();

        assert!(verify(&release, evil, Some(&signature), Some(&public_key)).is_err());

        // Even when the listed size is changed to match, the signature is what fails.
        release.installer_size = evil.len() as i64;
        assert_eq!(
            verify(&release, evil, Some(&signature), Some(&public_key)),
            Err("The installer's signature is not valid.".to_owned())
        );
    }

    #[test]
    fn without_a_key_a_release_with_no_digest_is_no_longer_trusted() {
        let release = feed::parse(&signed_json(false, None)).unwrap();

        assert!(verify(&release, INSTALLER, Some(""), Some("")).is_err());
    }

    #[test]
    fn a_signature_response_too_large_to_be_one_is_refused() {
        assert_eq!(check_signature_size(Some(88)), Ok(()));
        assert_eq!(check_signature_size(None), Ok(()));
        assert_eq!(check_signature_size(Some(1025)), Err("The installer's signature is not a signature.".to_owned()));
    }
}
