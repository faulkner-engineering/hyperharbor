use super::*;
use rcgen::{
    CertificateParams, DistinguishedName, DnType, ExtendedKeyUsagePurpose, IsCa, KeyPair,
    KeyUsagePurpose, SanType,
};
use std::cell::RefCell;

/// A certificate like the one the client makes, changed by `adjust`.
fn certificate(adjust: impl FnOnce(&mut CertificateParams)) -> Vec<u8> {
    let key = KeyPair::generate().unwrap();
    let mut params = CertificateParams::new(Vec::<String>::new()).unwrap();
    params.distinguished_name = DistinguishedName::new();
    params
        .distinguished_name
        .push(DnType::CommonName, SIGNER_NAME);
    params.is_ca = IsCa::ExplicitNoCa;
    params.key_usages = vec![KeyUsagePurpose::DigitalSignature];
    params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
    params.not_before = rcgen::date_time_ymd(2026, 10, 22);
    params.not_after = rcgen::date_time_ymd(2036, 10, 22);
    adjust(&mut params);
    params.self_signed(&key).unwrap().der().to_vec()
}

#[test]
fn the_signer_the_client_makes_is_accepted() {
    assert_eq!(check_signer(&certificate(|_| {})), Ok(()));
}

#[test]
fn a_certificate_that_could_vouch_for_others_or_for_a_server_is_refused() {
    let cases: Vec<(&str, Vec<u8>)> = vec![
        (
            "certificate authority",
            certificate(|p| p.is_ca = IsCa::Ca(rcgen::BasicConstraints::Unconstrained)),
        ),
        (
            "no basic constraints",
            certificate(|p| p.is_ca = IsCa::NoCa),
        ),
        (
            "code signing",
            certificate(|p| p.extended_key_usages = vec![ExtendedKeyUsagePurpose::CodeSigning]),
        ),
        (
            "server and client authentication",
            certificate(|p| {
                p.extended_key_usages = vec![
                    ExtendedKeyUsagePurpose::ServerAuth,
                    ExtendedKeyUsagePurpose::ClientAuth,
                ]
            }),
        ),
        (
            "any purpose",
            certificate(|p| p.extended_key_usages = vec![ExtendedKeyUsagePurpose::Any]),
        ),
        (
            "no purpose",
            certificate(|p| p.extended_key_usages = vec![]),
        ),
        (
            "certificate signing key",
            certificate(|p| {
                p.key_usages = vec![
                    KeyUsagePurpose::DigitalSignature,
                    KeyUsagePurpose::KeyCertSign,
                ]
            }),
        ),
        (
            "a host name",
            certificate(|p| {
                p.subject_alt_names = vec![SanType::DnsName("example.com".try_into().unwrap())]
            }),
        ),
        (
            "another name",
            certificate(|p| {
                p.distinguished_name = DistinguishedName::new();
                p.distinguished_name.push(DnType::CommonName, "example.com");
            }),
        ),
        (
            "an extra name part",
            certificate(|p| {
                p.distinguished_name
                    .push(DnType::OrganizationName, "Example")
            }),
        ),
        (
            "fifty years",
            certificate(|p| p.not_after = rcgen::date_time_ymd(2076, 10, 22)),
        ),
    ];
    for (case, der) in cases {
        let outcome = check_signer(&der);
        assert!(outcome.is_err(), "{case} was accepted");
        assert!(outcome
            .unwrap_err()
            .starts_with("The signing certificate was refused"));
    }
    assert!(check_signer(b"not a certificate").is_err());
    let mut trailing = certificate(|_| {});
    trailing.push(0);
    assert!(check_signer(&trailing).is_err());
}

#[test]
fn the_hand_written_name_and_extensions_parse_as_intended() {
    let name = name_der(SIGNER_NAME);
    let (rest, parsed) = X509Name::from_der(&name).unwrap();
    assert!(rest.is_empty());
    let names: Vec<&str> = parsed
        .iter_common_name()
        .map(|cn| cn.as_str().unwrap())
        .collect();
    assert_eq!(names, [SIGNER_NAME]);

    let (_, constraints) = BasicConstraints::from_der(&BASIC_CONSTRAINTS_END_ENTITY).unwrap();
    assert!(!constraints.ca);
    let (_, usage) = ExtendedKeyUsage::from_der(&EKU_SERVER_AUTH).unwrap();
    assert!(usage.server_auth && !usage.client_auth && usage.other.is_empty());
    let (_, key) = KeyUsage::from_der(&KEY_USAGE_SIGNING).unwrap();
    assert_eq!(key.flags, 1);
}

#[test]
fn only_the_clients_own_certificates_count_as_signers() {
    assert!(signer_expiry(&certificate(|_| {})).is_some());
    let other = certificate(|p| {
        p.distinguished_name = DistinguishedName::new();
        p.distinguished_name
            .push(DnType::CommonName, "Someone else");
    });
    assert_eq!(signer_expiry(&other), None);
    assert_eq!(signer_expiry(b"junk"), None);
}

#[test]
fn ready_needs_the_certificate_the_root_and_the_list() {
    let all = TrustFacts {
        rdpsign_available: true,
        has_signer: true,
        in_root: true,
        listed: true,
    };
    assert_eq!(decide(all), TrustStatus::Ready);
    for missing in [
        TrustFacts {
            has_signer: false,
            ..all
        },
        TrustFacts {
            in_root: false,
            ..all
        },
        TrustFacts {
            listed: false,
            ..all
        },
    ] {
        assert_eq!(decide(missing), TrustStatus::NotSetUp);
    }
    assert_eq!(
        decide(TrustFacts {
            rdpsign_available: false,
            ..all
        }),
        TrustStatus::Unsupported
    );
}

#[test]
fn the_status_reaches_the_page_as_a_tagged_object() {
    assert_eq!(
        serde_json::to_value(TrustStatus::NotSetUp).unwrap(),
        serde_json::json!({ "state": "notSetUp" })
    );
}

const A: &str = "9AA032E2C5206C5DFC14EE398D1C1510D6D670B9ED42571DDF5E134A966EE6DA";
const B: &str = "aa6b54815146d6b700040287a94c6898bfb4473d8349cdbc7a4517b0b9cf0639";

#[test]
fn list_edits_keep_other_entries_and_ignore_case() {
    let list = "5ECFA7E4EE36FF477890F84E967280870E602748, sha256:9aa032e2c5206c5dfc14ee398d1c1510d6d670b9ed42571ddf5e134a966ee6da";
    assert!(policy_lists(list, A));
    assert!(!policy_lists(list, B));

    assert_eq!(
        edit_policy(list, Some(B), &[A.to_string()]),
        format!(
            "5ECFA7E4EE36FF477890F84E967280870E602748,sha256:{}",
            B.to_uppercase()
        )
    );
    // Adding an entry that is there already keeps one copy.
    assert_eq!(edit_policy(list, Some(A), &[]).matches(',').count(), 1);
    assert_eq!(edit_policy("", None, &[A.to_string()]), "");
    assert_eq!(policy_entries(" ,a,, b ,"), ["a", "b"]);
}

#[derive(Default)]
struct FakeMachine {
    policy: RefCell<String>,
    roots: RefCell<Vec<String>>,
    calls: RefCell<Vec<String>>,
    fail_add: bool,
}

impl MachineTrust for FakeMachine {
    fn read_policy(&self, sid: &str) -> Result<String, String> {
        self.calls.borrow_mut().push(format!("read {sid}"));
        Ok(self.policy.borrow().clone())
    }
    fn write_policy(&self, sid: &str, list: &str) -> Result<(), String> {
        self.calls.borrow_mut().push(format!("write {sid} {list}"));
        *self.policy.borrow_mut() = list.to_string();
        Ok(())
    }
    fn root_signers(&self) -> Result<Vec<String>, String> {
        Ok(self.roots.borrow().clone())
    }
    fn add_root(&self, der: &[u8]) -> Result<(), String> {
        self.calls.borrow_mut().push("add root".to_string());
        if self.fail_add {
            return Err("add failed".to_string());
        }
        self.roots.borrow_mut().push(sha256_hex(der));
        Ok(())
    }
    fn remove_root(&self, sha256_hex: &str) -> Result<(), String> {
        self.calls
            .borrow_mut()
            .push(format!("remove root {sha256_hex}"));
        self.roots
            .borrow_mut()
            .retain(|r| !r.eq_ignore_ascii_case(sha256_hex));
        Ok(())
    }
}

const SID: &str = "S-1-5-21-1111111111-2222222222-3333333333-1001";

#[test]
fn trusting_adds_the_root_before_the_list_and_replaces_the_earlier_signer() {
    let der = certificate(|_| {});
    let new = sha256_hex(&der);
    // An earlier signer of this user, another user's signer, and an unrelated entry.
    let fake = FakeMachine {
        policy: RefCell::new(format!("sha256:{A},ABCDEF")),
        roots: RefCell::new(vec![A.to_string(), B.to_string()]),
        ..FakeMachine::default()
    };

    trust(&fake, &der, &new.to_lowercase(), SID).unwrap();

    assert_eq!(
        *fake.calls.borrow(),
        [
            format!("read {SID}"),
            "add root".to_string(),
            format!("write {SID} ABCDEF,sha256:{new}"),
            format!("remove root {A}"),
        ]
    );
    assert_eq!(*fake.roots.borrow(), [B.to_string(), new]);
}

#[test]
fn trusting_again_changes_nothing_else() {
    let der = certificate(|_| {});
    let hex = sha256_hex(&der);
    let fake = FakeMachine {
        policy: RefCell::new(format!("sha256:{hex}")),
        roots: RefCell::new(vec![hex.clone()]),
        ..FakeMachine::default()
    };
    trust(&fake, &der, &hex, SID).unwrap();
    assert_eq!(*fake.policy.borrow(), format!("sha256:{hex}"));
    assert!(!fake.calls.borrow().iter().any(|c| c.starts_with("remove")));
}

#[test]
fn a_changed_or_refused_certificate_changes_nothing() {
    let der = certificate(|_| {});
    let fake = FakeMachine::default();
    let changed = trust(&fake, &der, A, SID).unwrap_err();
    assert!(changed.contains("changed after it was written"));

    let ca = certificate(|p| p.is_ca = IsCa::Ca(rcgen::BasicConstraints::Unconstrained));
    assert!(trust(&fake, &ca, &sha256_hex(&ca), SID).is_err());
    assert!(fake.calls.borrow().is_empty());
}

#[test]
fn a_failed_root_add_leaves_the_list_alone() {
    let der = certificate(|_| {});
    let fake = FakeMachine {
        policy: RefCell::new("ABCDEF".to_string()),
        fail_add: true,
        ..FakeMachine::default()
    };
    assert_eq!(
        trust(&fake, &der, &sha256_hex(&der), SID),
        Err("add failed".to_string())
    );
    assert_eq!(*fake.policy.borrow(), "ABCDEF");
}

#[test]
fn untrusting_removes_only_this_users_signers_list_first() {
    let fake = FakeMachine {
        policy: RefCell::new(format!("ABCDEF,sha256:{A}")),
        roots: RefCell::new(vec![A.to_string(), B.to_string()]),
        ..FakeMachine::default()
    };
    untrust(&fake, SID).unwrap();
    assert_eq!(
        *fake.calls.borrow(),
        [
            format!("read {SID}"),
            format!("write {SID} ABCDEF"),
            format!("remove root {A}"),
        ]
    );
    assert_eq!(*fake.roots.borrow(), [B.to_string()]);
}

#[test]
fn arguments_are_parsed_exactly() {
    let args = |parts: &[&str]| parts.iter().map(|s| s.to_string()).collect::<Vec<_>>();
    assert_eq!(
        parse_args(&args(&[
            "--trust-rdp-signer",
            r"C:\t\s.cer",
            A,
            SID,
            r"C:\r.txt"
        ])),
        Some(TrustCommand::Trust {
            certificate: PathBuf::from(r"C:\t\s.cer"),
            sha256: A.to_string(),
            sid: SID.to_string(),
            result: PathBuf::from(r"C:\r.txt"),
        })
    );
    assert_eq!(
        parse_args(&args(&["--untrust-rdp-signer", SID, r"C:\r.txt"])),
        Some(TrustCommand::Untrust {
            sid: SID.to_string(),
            result: PathBuf::from(r"C:\r.txt"),
        })
    );
    for other in [
        &["--trust-rdp-signer", "c", "nothex", SID, "r"][..],
        &["--trust-rdp-signer", "c", A, r"S-1-5-21-1\..\x", "r"],
        &["--untrust-rdp-signer", r"S-1-5\Software", "r"],
        &["--untrust-rdp-signer", "S-1-", "r"],
        &["--untrust-rdp-signer", SID],
        &["--setup-camera", "a", "b"],
        &[],
    ] {
        assert_eq!(parse_args(&args(other)), None, "{other:?}");
    }
}
