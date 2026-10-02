//! TLS configuration for talking to hosts. Host certificates are self-signed, so instead of
//! chain validation the client either pins a known certificate (after pairing) or records the
//! presented certificate so the pairing exchange can bind it (during pairing). Handshake
//! signatures are always verified.

use std::sync::{Arc, Mutex};

use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::crypto::{verify_tls12_signature, verify_tls13_signature, CryptoProvider};
use rustls::pki_types::{CertificateDer, ServerName, UnixTime};
use rustls::{ClientConfig, DigitallySignedStruct, SignatureScheme};
use sha2::{Digest, Sha256};

use crate::identity::ClientIdentity;

fn provider() -> Arc<CryptoProvider> {
    Arc::new(rustls::crypto::aws_lc_rs::default_provider())
}

/// What the verifier accepts.
#[derive(Debug)]
enum Expectation {
    /// Accept only a certificate whose DER hashes to this value.
    Pinned([u8; 32]),
    /// Accept any certificate and record it for the pairing transcript.
    Capture(Arc<Mutex<Option<Vec<u8>>>>),
}

#[derive(Debug)]
struct HostVerifier {
    expectation: Expectation,
    provider: Arc<CryptoProvider>,
}

impl ServerCertVerifier for HostVerifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        match &self.expectation {
            Expectation::Pinned(expected) => {
                let actual: [u8; 32] = Sha256::digest(end_entity.as_ref()).into();
                if &actual == expected {
                    Ok(ServerCertVerified::assertion())
                } else {
                    Err(rustls::Error::General(
                        "the host certificate does not match the paired certificate".into(),
                    ))
                }
            }
            Expectation::Capture(slot) => {
                *slot.lock().unwrap() = Some(end_entity.to_vec());
                Ok(ServerCertVerified::assertion())
            }
        }
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls12_signature(
            message,
            cert,
            dss,
            &self.provider.signature_verification_algorithms,
        )
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls13_signature(
            message,
            cert,
            dss,
            &self.provider.signature_verification_algorithms,
        )
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.provider
            .signature_verification_algorithms
            .supported_schemes()
    }
}

fn build(identity: &ClientIdentity, expectation: Expectation) -> ClientConfig {
    let provider = provider();
    ClientConfig::builder_with_provider(provider.clone())
        .with_safe_default_protocol_versions()
        .expect("default protocol versions are supported")
        .dangerous()
        .with_custom_certificate_verifier(Arc::new(HostVerifier {
            expectation,
            provider,
        }))
        .with_client_auth_cert(vec![identity.certificate_der()], identity.private_key_der())
        .expect("the client key matches its certificate")
}

/// mTLS configuration that accepts only the paired host certificate.
pub fn pinned(identity: &ClientIdentity, host_certificate_hash: [u8; 32]) -> ClientConfig {
    build(identity, Expectation::Pinned(host_certificate_hash))
}

/// Configuration for pairing. The returned slot receives the certificate the host presented.
pub fn capturing(identity: &ClientIdentity) -> (ClientConfig, Arc<Mutex<Option<Vec<u8>>>>) {
    let slot = Arc::new(Mutex::new(None));
    (build(identity, Expectation::Capture(slot.clone())), slot)
}
