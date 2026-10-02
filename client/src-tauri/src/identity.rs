//! The client's TLS identity: a P-256 key pair and self-signed certificate created on first run.
//! The private key is kept in the operating system credential store; the certificate, which is
//! public, is kept in the app config directory.

use std::path::{Path, PathBuf};

use rcgen::{CertificateParams, DnType, ExtendedKeyUsagePurpose, KeyPair, PKCS_ECDSA_P256_SHA256};
use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use sha2::{Digest, Sha256};

use crate::error::ClientError;

const KEYRING_SERVICE: &str = "HyperHarbor";
const KEYRING_ACCOUNT: &str = "client-tls-key";
const CERTIFICATE_FILE: &str = "client-certificate.pem";

pub struct ClientIdentity {
    pub certificate_pem: String,
    certificate_der: Vec<u8>,
    key_der: Vec<u8>,
}

impl ClientIdentity {
    /// Loads the identity, creating one if the key or certificate is missing or they do not match.
    pub fn load_or_create(config_dir: &Path, host_name: &str) -> Result<Self, ClientError> {
        let path = config_dir.join(CERTIFICATE_FILE);
        if let Some(identity) = Self::load(&path)? {
            return Ok(identity);
        }
        Self::create(&path, host_name)
    }

    pub fn certificate_der(&self) -> CertificateDer<'static> {
        CertificateDer::from(self.certificate_der.clone())
    }

    pub fn private_key_der(&self) -> PrivateKeyDer<'static> {
        PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(self.key_der.clone()))
    }

    /// SHA-256 of the certificate DER, as used in the pairing transcript.
    pub fn certificate_hash(&self) -> [u8; 32] {
        Sha256::digest(&self.certificate_der).into()
    }

    fn load(path: &PathBuf) -> Result<Option<Self>, ClientError> {
        let Ok(certificate_pem) = std::fs::read_to_string(path) else {
            return Ok(None);
        };
        let key_pem = match keyring_entry()?.get_password() {
            Ok(pem) => pem,
            Err(keyring::Error::NoEntry) => return Ok(None),
            Err(error) => return Err(ClientError::Storage(error.to_string())),
        };

        let Ok(key_pair) = KeyPair::from_pem(&key_pem) else {
            return Ok(None);
        };
        let Some(certificate_der) = pem_to_der(&certificate_pem) else {
            return Ok(None);
        };

        // The certificate must carry this key; otherwise TLS would fail with a confusing error.
        if !public_keys_match(&certificate_der, &key_pair) {
            return Ok(None);
        }

        Ok(Some(Self {
            certificate_pem,
            certificate_der,
            key_der: key_pair.serialize_der(),
        }))
    }

    fn create(path: &Path, host_name: &str) -> Result<Self, ClientError> {
        let (identity, key_pem) = Self::generate(host_name)?;

        keyring_entry()?
            .set_password(&key_pem)
            .map_err(|e| ClientError::Storage(e.to_string()))?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| ClientError::Storage(e.to_string()))?;
        }
        std::fs::write(path, &identity.certificate_pem)
            .map_err(|e| ClientError::Storage(e.to_string()))?;

        Ok(identity)
    }

    /// Creates a new key pair and certificate without storing them. Returns the key as PEM.
    pub fn generate(host_name: &str) -> Result<(Self, String), ClientError> {
        let key_pair = KeyPair::generate_for(&PKCS_ECDSA_P256_SHA256)
            .map_err(|e| ClientError::Storage(e.to_string()))?;

        let mut params = CertificateParams::new(Vec::<String>::new())
            .map_err(|e| ClientError::Storage(e.to_string()))?;
        params.distinguished_name.push(
            DnType::CommonName,
            format!("HyperHarbor Client {host_name}"),
        );
        params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ClientAuth];
        params.not_before = rcgen::date_time_ymd(2025, 1, 1);
        params.not_after = rcgen::date_time_ymd(2045, 1, 1);

        let certificate = params
            .self_signed(&key_pair)
            .map_err(|e| ClientError::Storage(e.to_string()))?;

        Ok((
            Self {
                certificate_pem: certificate.pem(),
                certificate_der: certificate.der().to_vec(),
                key_der: key_pair.serialize_der(),
            },
            key_pair.serialize_pem(),
        ))
    }
}

fn keyring_entry() -> Result<keyring::Entry, ClientError> {
    keyring::Entry::new(KEYRING_SERVICE, KEYRING_ACCOUNT)
        .map_err(|e| ClientError::Storage(e.to_string()))
}

/// Decodes the first CERTIFICATE block of a PEM string.
pub fn pem_to_der(pem: &str) -> Option<Vec<u8>> {
    use rustls::pki_types::pem::PemObject;
    CertificateDer::from_pem_slice(pem.as_bytes())
        .ok()
        .map(|der| der.to_vec())
}

/// Compares the certificate's SubjectPublicKeyInfo with the key pair's public key.
fn public_keys_match(certificate_der: &[u8], key_pair: &KeyPair) -> bool {
    let public_key = key_pair.public_key_raw();
    certificate_der
        .windows(public_key.len())
        .any(|window| window == public_key)
}
