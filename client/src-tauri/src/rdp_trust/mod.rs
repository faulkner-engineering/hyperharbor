//! Signed .rdp files, so Remote Desktop opens HyperHarbor's connections without a security dialog.
//!
//! Since the April 2026 Windows security update (CVE-2026-26151), mstsc shows "Remote Desktop
//! Connection security warning" for every .rdp file it cannot trust, with every redirection
//! (clipboard, microphone, camera, WebAuthn) off until the user ticks it, every time. A file opens
//! without the dialog, with its redirections as written, only when it is signed by a certificate
//! that both chains to a trusted root and has its thumbprint on the "trusted .rdp publishers" policy
//! list. Found on build 26100 (2026-10-22): the list alone still showed "Unknown publisher" for a
//! self-signed certificate; adding the same certificate to a root store made the dialog go away.
//!
//! So each client makes its own signing certificate in the user's store (key not exportable,
//! Server Authentication only, not a certificate authority, no host name), and a one-time elevated
//! copy of the client (`--trust-rdp-signer`) adds it to the machine's Trusted Root store and its
//! SHA-256 thumbprint to the user's policy list. rdpsign.exe then signs each file the client writes.
//! rdpsign refuses a Document Signing certificate (0x8007000d); Server Authentication is the purpose
//! that grants least elsewhere, since the certificate names no host.
//!
//! The signature covers only Microsoft's "secure" settings (address, credentials prompts, audio,
//! clipboard, drives, WebAuthn, and so on). Camera, microphone capture, and monitor lines stay
//! outside it.

pub mod system;

use std::path::PathBuf;
use x509_parser::prelude::*;

/// The subject common name of every signing certificate the client makes.
pub const SIGNER_NAME: &str = "HyperHarbor connection signing";

/// Years a new signing certificate is valid for.
pub const SIGNER_YEARS: u16 = 10;

/// What the page shows: whether this device signs its .rdp files with a trusted certificate.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase", tag = "state")]
pub enum TrustStatus {
    /// rdpsign.exe is missing, so files cannot be signed here.
    Unsupported,
    NotSetUp,
    Ready,
}

/// What the status is decided from.
#[derive(Debug, Clone, Copy, Default)]
pub struct TrustFacts {
    pub rdpsign_available: bool,
    /// A usable signing certificate with its private key is in the user's store.
    pub has_signer: bool,
    /// That certificate is in the machine's Trusted Root store.
    pub in_root: bool,
    /// Its SHA-256 thumbprint is on the user's or the machine's trusted publishers list.
    pub listed: bool,
}

pub fn decide(facts: TrustFacts) -> TrustStatus {
    if !facts.rdpsign_available {
        TrustStatus::Unsupported
    } else if facts.has_signer && facts.in_root && facts.listed {
        TrustStatus::Ready
    } else {
        TrustStatus::NotSetUp
    }
}

/// The policy list entry for a certificate: "sha256:" and the uppercase hex of its SHA-256.
pub fn policy_entry(sha256_hex: &str) -> String {
    format!("sha256:{}", sha256_hex.to_ascii_uppercase())
}

/// The entries of a TrustedCertThumbprints value (comma-separated).
pub fn policy_entries(list: &str) -> Vec<String> {
    list.split(',')
        .map(str::trim)
        .filter(|entry| !entry.is_empty())
        .map(str::to_string)
        .collect()
}

/// Whether `list` names the certificate with this SHA-256 (entries ignore case).
pub fn policy_lists(list: &str, sha256_hex: &str) -> bool {
    let wanted = policy_entry(sha256_hex);
    policy_entries(list)
        .iter()
        .any(|entry| entry.eq_ignore_ascii_case(&wanted))
}

/// `list` without the entries for `remove` (SHA-256 hex values), plus the entry for `add` when it
/// is not there yet. Every other entry is kept as it was.
pub fn edit_policy(list: &str, add: Option<&str>, remove: &[String]) -> String {
    let removed: Vec<String> = remove.iter().map(|hex| policy_entry(hex)).collect();
    let mut entries: Vec<String> = policy_entries(list)
        .into_iter()
        .filter(|entry| !removed.iter().any(|r| r.eq_ignore_ascii_case(entry)))
        .collect();
    if let Some(hex) = add {
        let entry = policy_entry(hex);
        if !entries.iter().any(|e| e.eq_ignore_ascii_case(&entry)) {
            entries.push(entry);
        }
    }
    entries.join(",")
}

/// The SHA-256 of `der` as uppercase hex.
pub fn sha256_hex(der: &[u8]) -> String {
    use sha2::{Digest, Sha256};
    hex::encode_upper(Sha256::digest(der))
}

/// Whether `der` is a certificate the elevated copy may add to the machine's Trusted Root store:
/// self-issued by the name `SIGNER_NAME`, an end entity (basic constraints present, critical, not a
/// CA), for Server Authentication only, signing only, with no host names and no other extensions.
/// A root that is not a CA cannot vouch for any other certificate, and one that names no host
/// cannot stand in for a server. The certificate is checked because the file the elevated copy reads
/// was written by an unelevated process.
pub fn check_signer(der: &[u8]) -> Result<(), String> {
    let refuse = |why: &str| Err(format!("The signing certificate was refused: {why}."));
    let Ok((rest, cert)) = X509Certificate::from_der(der) else {
        return refuse("it is not a certificate");
    };
    if !rest.is_empty() {
        return refuse("it has data after the certificate");
    }
    if cert.version() != X509Version::V3 {
        return refuse("it is not a version 3 certificate");
    }
    let names: Vec<&str> = cert
        .subject()
        .iter_attributes()
        .map(|attribute| attribute.as_str().unwrap_or(""))
        .collect();
    let common_names: Vec<&str> = cert
        .subject()
        .iter_common_name()
        .map(|cn| cn.as_str().unwrap_or(""))
        .collect();
    if names.len() != 1 || common_names != [SIGNER_NAME] {
        return refuse("its name is not HyperHarbor's");
    }
    if cert.subject().as_raw() != cert.issuer().as_raw() {
        return refuse("it is not self-issued");
    }
    match cert.basic_constraints() {
        Ok(Some(constraints)) if constraints.critical && !constraints.value.ca => {}
        _ => return refuse("it could act as a certificate authority"),
    }
    match cert.extended_key_usage() {
        Ok(Some(usage))
            if usage.value.server_auth
                && !usage.value.any
                && !usage.value.client_auth
                && !usage.value.code_signing
                && !usage.value.email_protection
                && !usage.value.time_stamping
                && !usage.value.ocsp_signing
                && usage.value.other.is_empty() => {}
        _ => return refuse("its purpose is not Server Authentication alone"),
    }
    match cert.key_usage() {
        // Bit 0 alone: digital signature.
        Ok(Some(usage)) if usage.value.flags == 1 => {}
        Ok(None) => {}
        _ => return refuse("its key may be used for more than signing"),
    }
    const ALLOWED: [&str; 5] = [
        "2.5.29.19", // basic constraints
        "2.5.29.37", // extended key usage
        "2.5.29.15", // key usage
        "2.5.29.14", // subject key identifier
        "2.5.29.35", // authority key identifier
    ];
    if let Some(other) = cert
        .extensions()
        .iter()
        .find(|e| !ALLOWED.contains(&e.oid.to_id_string().as_str()))
    {
        return refuse(&format!("it has an unexpected extension ({})", other.oid));
    }
    let validity = cert.validity();
    let seconds = validity.not_after.timestamp() - validity.not_before.timestamp();
    if seconds > i64::from(SIGNER_YEARS + 1) * 366 * 24 * 60 * 60 {
        return refuse("it is valid for too long");
    }
    Ok(())
}

/// The DER extension values of a new signing certificate, so no encoder is needed.
/// Basic constraints with every field at its default: not a certificate authority.
pub const BASIC_CONSTRAINTS_END_ENTITY: [u8; 2] = [0x30, 0x00];
/// Extended key usage: Server Authentication (1.3.6.1.5.5.7.3.1) only.
pub const EKU_SERVER_AUTH: [u8; 12] = [
    0x30, 0x0A, 0x06, 0x08, 0x2B, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x01,
];
/// Key usage: digital signature only (a bit string with seven unused bits).
pub const KEY_USAGE_SIGNING: [u8; 4] = [0x03, 0x02, 0x07, 0x80];

/// The DER of the name "CN=<common_name>" (one UTF8String attribute). Short names only.
pub fn name_der(common_name: &str) -> Vec<u8> {
    fn tlv(tag: u8, content: &[u8]) -> Vec<u8> {
        assert!(content.len() < 128, "only short DER lengths are written");
        let mut out = vec![tag, content.len() as u8];
        out.extend_from_slice(content);
        out
    }
    let oid = tlv(0x06, &[0x55, 0x04, 0x03]);
    let value = tlv(0x0C, common_name.as_bytes());
    let attribute = tlv(0x30, &[oid, value].concat());
    tlv(0x30, &tlv(0x31, &attribute))
}

/// When a certificate in the user's store is one of the client's signers (self-issued, named
/// `SIGNER_NAME`): its expiry as a Unix timestamp. None for any other certificate.
pub fn signer_expiry(der: &[u8]) -> Option<i64> {
    let (_, cert) = X509Certificate::from_der(der).ok()?;
    let named = cert
        .subject()
        .iter_common_name()
        .any(|cn| cn.as_str() == Ok(SIGNER_NAME));
    (named && cert.subject().as_raw() == cert.issuer().as_raw())
        .then(|| cert.validity().not_after.timestamp())
}

/// What the elevated copy does to the machine, so its order is tested without administrator rights.
pub trait MachineTrust {
    /// The TrustedCertThumbprints value in the user's policy key (empty when it is not set).
    fn read_policy(&self, sid: &str) -> Result<String, String>;
    /// Writes the value; an empty list removes it.
    fn write_policy(&self, sid: &str, list: &str) -> Result<(), String>;
    /// The SHA-256 (hex) of every certificate named `SIGNER_NAME` in the machine's Trusted Root store.
    fn root_signers(&self) -> Result<Vec<String>, String>;
    fn add_root(&self, der: &[u8]) -> Result<(), String>;
    fn remove_root(&self, sha256_hex: &str) -> Result<(), String>;
}

/// The HyperHarbor signers on the user's list: entries that name a `SIGNER_NAME` root certificate.
fn listed_signers(system: &dyn MachineTrust, list: &str) -> Result<Vec<String>, String> {
    Ok(system
        .root_signers()?
        .into_iter()
        .filter(|hex| policy_lists(list, hex))
        .collect())
}

/// Trusts the certificate `der` for the user `sid`: adds it to the Trusted Root store and its
/// thumbprint to the user's list, then removes an earlier HyperHarbor signer of the same user (from
/// the list first, then from the root store). Other users' signers and other list entries stay.
/// `expected_sha256` is what the unelevated client computed, so a file changed after it was written
/// is refused.
pub fn trust(
    system: &dyn MachineTrust,
    der: &[u8],
    expected_sha256: &str,
    sid: &str,
) -> Result<(), String> {
    let sha256 = sha256_hex(der);
    if !sha256.eq_ignore_ascii_case(expected_sha256) {
        return Err("The signing certificate changed after it was written.".to_string());
    }
    check_signer(der)?;
    let list = system.read_policy(sid)?;
    let earlier: Vec<String> = listed_signers(system, &list)?
        .into_iter()
        .filter(|hex| !hex.eq_ignore_ascii_case(&sha256))
        .collect();
    system.add_root(der)?;
    system.write_policy(sid, &edit_policy(&list, Some(&sha256), &earlier))?;
    for hex in &earlier {
        system.remove_root(hex)?;
    }
    Ok(())
}

/// Stops trusting the user's HyperHarbor signers: takes them off the user's list, then out of the
/// Trusted Root store.
pub fn untrust(system: &dyn MachineTrust, sid: &str) -> Result<(), String> {
    let list = system.read_policy(sid)?;
    let signers = listed_signers(system, &list)?;
    system.write_policy(sid, &edit_policy(&list, None, &signers))?;
    for hex in &signers {
        system.remove_root(hex)?;
    }
    Ok(())
}

/// A job for an elevated copy of the client.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TrustCommand {
    Trust {
        certificate: PathBuf,
        sha256: String,
        sid: String,
        result: PathBuf,
    },
    Untrust {
        sid: String,
        result: PathBuf,
    },
}

/// A string SID of the form S-1-5-21-...: the elevated copy writes under HKEY_USERS\<sid>.
pub fn is_valid_sid(sid: &str) -> bool {
    sid.len() <= 184
        && sid.starts_with("S-1-")
        && sid[4..]
            .split('-')
            .all(|part| !part.is_empty() && part.bytes().all(|b| b.is_ascii_digit()))
}

/// The command in an elevated copy's arguments (the program name excluded), if any.
pub fn parse_args(args: &[String]) -> Option<TrustCommand> {
    match args {
        [flag, certificate, sha256, sid, result] if flag == "--trust-rdp-signer" => {
            let hex_ok = sha256.len() == 64 && sha256.bytes().all(|b| b.is_ascii_hexdigit());
            (hex_ok && is_valid_sid(sid)).then(|| TrustCommand::Trust {
                certificate: PathBuf::from(certificate),
                sha256: sha256.clone(),
                sid: sid.clone(),
                result: PathBuf::from(result),
            })
        }
        [flag, sid, result] if flag == "--untrust-rdp-signer" => {
            is_valid_sid(sid).then(|| TrustCommand::Untrust {
                sid: sid.clone(),
                result: PathBuf::from(result),
            })
        }
        _ => None,
    }
}

#[cfg(test)]
mod tests;
