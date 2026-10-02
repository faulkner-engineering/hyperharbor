//! SPAKE2 over the RFC 3526 3072-bit MODP group, as specified in docs/pairing.md.
//! The client is party A (uses M); the host is party B (uses N).

use std::sync::OnceLock;

use hmac::{Hmac, KeyInit, Mac};
use num_bigint::BigUint;
use num_traits::One;
use sha2::{Digest, Sha256, Sha512};
use subtle::ConstantTimeEq;

/// Length of an encoded group element or scalar in bytes.
pub const ELEMENT_LENGTH: usize = 384;

const PRIME_HEX: &str = concat!(
    "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74",
    "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437",
    "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED",
    "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05",
    "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB",
    "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B",
    "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718",
    "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33",
    "A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7",
    "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864",
    "D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2",
    "08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF",
);

const TRANSCRIPT_LABEL: &str = "HyperHarbor-PAKE-v1";

struct Group {
    p: BigUint,
    q: BigUint,
    g: BigUint,
    m: BigUint,
    n: BigUint,
}

fn group() -> &'static Group {
    static GROUP: OnceLock<Group> = OnceLock::new();
    GROUP.get_or_init(|| {
        let p = BigUint::parse_bytes(PRIME_HEX.as_bytes(), 16).expect("valid prime");
        let q = (&p - BigUint::one()) >> 1;
        let m = hash_to_group(&p, "HyperHarbor SPAKE2 MODP-3072 M");
        let n = hash_to_group(&p, "HyperHarbor SPAKE2 MODP-3072 N");
        Group {
            p,
            q,
            g: BigUint::from(2u32),
            m,
            n,
        }
    })
}

/// (int(expand(label)) mod p)^2 mod p, where expand concatenates SHA-512(label || i) for i = 0..6.
fn hash_to_group(p: &BigUint, label: &str) -> BigUint {
    let mut expanded = Vec::with_capacity(7 * 64);
    for i in 0u8..7 {
        let mut hasher = Sha512::new();
        hasher.update(label.as_bytes());
        hasher.update([i]);
        expanded.extend_from_slice(&hasher.finalize());
    }
    let reduced = BigUint::from_bytes_be(&expanded) % p;
    reduced.modpow(&BigUint::from(2u32), p)
}

/// w = int(SHA-512("HyperHarbor SPAKE2 w" || 0x00 || pairingId || 0x00 || pin)) mod q.
fn password_scalar(pairing_id: &str, pin: &str) -> BigUint {
    let mut hasher = Sha512::new();
    hasher.update(b"HyperHarbor SPAKE2 w");
    hasher.update([0]);
    hasher.update(pairing_id.to_ascii_lowercase().as_bytes());
    hasher.update([0]);
    hasher.update(pin.as_bytes());
    BigUint::from_bytes_be(&hasher.finalize()) % &group().q
}

fn random_scalar() -> BigUint {
    let mut bytes = [0u8; ELEMENT_LENGTH + 64];
    getrandom::fill(&mut bytes).expect("operating system random number generator");
    let q_minus_one = &group().q - BigUint::one();
    (BigUint::from_bytes_be(&bytes) % q_minus_one) + BigUint::one()
}

/// Encodes an integer as a 384-byte unsigned big-endian value.
pub fn encode(value: &BigUint) -> Vec<u8> {
    let raw = value.to_bytes_be();
    assert!(
        raw.len() <= ELEMENT_LENGTH,
        "value does not fit in 384 bytes"
    );
    let mut encoded = vec![0u8; ELEMENT_LENGTH - raw.len()];
    encoded.extend_from_slice(&raw);
    encoded
}

/// Decodes a received share and checks that it is in the order-q subgroup.
fn decode_share(encoded: &[u8]) -> Option<BigUint> {
    if encoded.len() != ELEMENT_LENGTH {
        return None;
    }
    let group = group();
    let value = BigUint::from_bytes_be(encoded);
    let p_minus_one = &group.p - BigUint::one();
    if value <= BigUint::one() || value >= p_minus_one || !value.modpow(&group.q, &group.p).is_one()
    {
        return None;
    }
    Some(value)
}

fn hmac_sha256(key: &[u8], data: &[u8]) -> [u8; 32] {
    let mut mac =
        <Hmac<Sha256> as KeyInit>::new_from_slice(key).expect("HMAC accepts any key length");
    mac.update(data);
    mac.finalize().into_bytes().into()
}

fn append_field(transcript: &mut Vec<u8>, field: &[u8]) {
    transcript.extend_from_slice(&(field.len() as u64).to_le_bytes());
    transcript.extend_from_slice(field);
}

struct TranscriptInputs<'a> {
    pairing_id: &'a str,
    client_certificate_hash: &'a [u8],
    host_certificate_hash: &'a [u8],
    client_share: &'a BigUint,
    host_share: &'a BigUint,
    shared_element: &'a BigUint,
    w: &'a BigUint,
}

/// Returns (cA, cB).
fn confirmations(inputs: &TranscriptInputs<'_>) -> ([u8; 32], [u8; 32]) {
    let mut transcript = Vec::new();
    append_field(&mut transcript, TRANSCRIPT_LABEL.as_bytes());
    append_field(
        &mut transcript,
        inputs.pairing_id.to_ascii_lowercase().as_bytes(),
    );
    append_field(&mut transcript, inputs.client_certificate_hash);
    append_field(&mut transcript, inputs.host_certificate_hash);
    append_field(&mut transcript, &encode(inputs.client_share));
    append_field(&mut transcript, &encode(inputs.host_share));
    append_field(&mut transcript, &encode(inputs.shared_element));
    append_field(&mut transcript, &encode(inputs.w));

    let ka = Sha256::digest(&transcript);
    let kc_a = hmac_sha256(&ka, b"client confirmation");
    let kc_b = hmac_sha256(&ka, b"host confirmation");
    (
        hmac_sha256(&kc_a, &transcript),
        hmac_sha256(&kc_b, &transcript),
    )
}

/// Client side of the exchange.
pub struct ClientExchange {
    /// X, to send as clientShare.
    pub client_share: Vec<u8>,
    /// cA, to send as clientConfirmation.
    pub client_confirmation: [u8; 32],
    expected_host_confirmation: [u8; 32],
}

impl ClientExchange {
    /// Runs the client side given the host share Y. Returns None if Y is not a valid group element.
    pub fn new(
        pairing_id: &str,
        pin: &str,
        host_share: &[u8],
        client_certificate_hash: &[u8],
        host_certificate_hash: &[u8],
    ) -> Option<Self> {
        Self::with_secret(
            pairing_id,
            pin,
            &random_scalar(),
            host_share,
            client_certificate_hash,
            host_certificate_hash,
        )
    }

    fn with_secret(
        pairing_id: &str,
        pin: &str,
        x: &BigUint,
        host_share: &[u8],
        client_certificate_hash: &[u8],
        host_certificate_hash: &[u8],
    ) -> Option<Self> {
        let group = group();
        let y_share = decode_share(host_share)?;
        let w = password_scalar(pairing_id, pin);

        let x_share = group.g.modpow(x, &group.p) * group.m.modpow(&w, &group.p) % &group.p;
        let unmasked = &y_share * group.n.modpow(&(&group.q - &w), &group.p) % &group.p;
        let k = unmasked.modpow(x, &group.p);

        let (c_a, c_b) = confirmations(&TranscriptInputs {
            pairing_id,
            client_certificate_hash,
            host_certificate_hash,
            client_share: &x_share,
            host_share: &y_share,
            shared_element: &k,
            w: &w,
        });

        Some(Self {
            client_share: encode(&x_share),
            client_confirmation: c_a,
            expected_host_confirmation: c_b,
        })
    }

    /// Checks the host's confirmation cB in constant time.
    pub fn verify_host_confirmation(&self, host_confirmation: &[u8]) -> bool {
        host_confirmation.len() == 32
            && bool::from(self.expected_host_confirmation.ct_eq(host_confirmation))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    const VECTORS: &str = include_str!("../../../host/tests/Host.Tests/Pairing/Spake2Vectors.json");

    fn vectors() -> HashMap<String, String> {
        serde_json::from_str(VECTORS).expect("valid vector file")
    }

    fn upper_hex(bytes: &[u8]) -> String {
        hex::encode_upper(bytes)
    }

    #[test]
    fn group_elements_match_dotnet() {
        let v = vectors();
        assert_eq!(upper_hex(&encode(&group().m)), v["M"]);
        assert_eq!(upper_hex(&encode(&group().n)), v["N"]);
    }

    #[test]
    fn password_scalar_matches_dotnet() {
        let v = vectors();
        assert_eq!(
            upper_hex(&encode(&password_scalar(&v["pairingId"], &v["pin"]))),
            v["w"]
        );
    }

    #[test]
    fn client_exchange_matches_dotnet_vectors() {
        let v = vectors();
        let x = BigUint::from_bytes_be(&hex::decode(&v["x"]).unwrap());
        let exchange = ClientExchange::with_secret(
            &v["pairingId"],
            &v["pin"],
            &x,
            &hex::decode(&v["Y"]).unwrap(),
            &hex::decode(&v["clientCertificateHash"]).unwrap(),
            &hex::decode(&v["hostCertificateHash"]).unwrap(),
        )
        .expect("valid host share");

        assert_eq!(upper_hex(&exchange.client_share), v["X"]);
        assert_eq!(
            upper_hex(&exchange.client_confirmation),
            v["clientConfirmation"]
        );
        assert!(exchange.verify_host_confirmation(&hex::decode(&v["hostConfirmation"]).unwrap()));
    }

    #[test]
    fn wrong_pin_fails_host_confirmation() {
        let v = vectors();
        let exchange = ClientExchange::new(
            &v["pairingId"],
            "000000",
            &hex::decode(&v["Y"]).unwrap(),
            &hex::decode(&v["clientCertificateHash"]).unwrap(),
            &hex::decode(&v["hostCertificateHash"]).unwrap(),
        )
        .unwrap();
        assert!(!exchange.verify_host_confirmation(&hex::decode(&v["hostConfirmation"]).unwrap()));
        assert_ne!(
            upper_hex(&exchange.client_confirmation),
            v["clientConfirmation"]
        );
    }

    #[test]
    fn invalid_host_shares_are_rejected() {
        let p = &group().p;
        for bad in [BigUint::from(0u32), BigUint::one(), p - BigUint::one()] {
            assert!(decode_share(&encode(&bad)).is_none());
        }
        assert!(decode_share(&[1u8; 10]).is_none());
        assert!(
            ClientExchange::new("id", "123456", &[0u8; ELEMENT_LENGTH], &[0; 32], &[0; 32])
                .is_none()
        );
    }
}
