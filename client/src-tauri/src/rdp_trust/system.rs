//! The Windows side of signed .rdp files: the signing certificate in the user's store, the
//! machine's Trusted Root store, the trusted publishers policy value, and rdpsign.exe.
#![allow(unsafe_code)]

use super::{
    decide, policy_lists, sha256_hex, signer_expiry, MachineTrust, TrustCommand, TrustFacts,
    TrustStatus, BASIC_CONSTRAINTS_END_ENTITY, EKU_SERVER_AUTH, KEY_USAGE_SIGNING, SIGNER_NAME,
    SIGNER_YEARS,
};
use std::ffi::c_void;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use windows::core::{BOOL, HSTRING, PCWSTR, PSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, LocalFree, HANDLE, HLOCAL};
use windows::Win32::Security::Authorization::ConvertSidToStringSidW;
use windows::Win32::Security::Cryptography::{
    szOID_BASIC_CONSTRAINTS2, szOID_ENHANCED_KEY_USAGE, szOID_KEY_USAGE, szOID_RSA_SHA256RSA,
    CertAddCertificateContextToStore, CertAddEncodedCertificateToStore, CertCloseStore,
    CertCreateSelfSignCertificate, CertDeleteCertificateFromStore, CertDuplicateCertificateContext,
    CertEnumCertificatesInStore, CertFreeCertificateContext, CertGetCertificateContextProperty,
    CertOpenStore, CertSetCertificateContextProperty, CryptAcquireCertificatePrivateKey,
    NCryptCreatePersistedKey, NCryptDeleteKey, NCryptFinalizeKey, NCryptFreeObject,
    NCryptOpenStorageProvider, NCryptSetProperty, CERT_CONTEXT, CERT_CREATE_SELFSIGN_FLAGS,
    CERT_EXTENSION, CERT_EXTENSIONS, CERT_FRIENDLY_NAME_PROP_ID, CERT_HASH_PROP_ID, CERT_KEY_SPEC,
    CERT_OPEN_STORE_FLAGS, CERT_QUERY_ENCODING_TYPE, CERT_STORE_ADD_NEW,
    CERT_STORE_ADD_REPLACE_EXISTING, CERT_STORE_OPEN_EXISTING_FLAG, CERT_STORE_PROV_SYSTEM_W,
    CERT_STORE_READONLY_FLAG, CERT_SYSTEM_STORE_CURRENT_USER, CERT_SYSTEM_STORE_LOCAL_MACHINE,
    CRYPT_ACQUIRE_FLAGS, CRYPT_ACQUIRE_ONLY_NCRYPT_KEY_FLAG, CRYPT_ACQUIRE_SILENT_FLAG,
    CRYPT_ALGORITHM_IDENTIFIER, CRYPT_INTEGER_BLOB, CRYPT_KEY_FLAGS, CRYPT_KEY_PROV_INFO,
    HCERTSTORE, HCRYPTPROV_OR_NCRYPT_KEY_HANDLE, MS_KEY_STORAGE_PROVIDER,
    NCRYPT_ALLOW_SIGNING_FLAG, NCRYPT_EXPORT_POLICY_PROPERTY, NCRYPT_FLAGS, NCRYPT_HANDLE,
    NCRYPT_KEY_HANDLE, NCRYPT_KEY_USAGE_PROPERTY, NCRYPT_LENGTH_PROPERTY, NCRYPT_PROV_HANDLE,
    NCRYPT_RSA_ALGORITHM, NCRYPT_SILENT_FLAG, PKCS_7_ASN_ENCODING, X509_ASN_ENCODING,
};
use windows::Win32::Security::{GetTokenInformation, TokenUser, TOKEN_QUERY, TOKEN_USER};
use windows::Win32::System::Registry::{
    RegCloseKey, RegCreateKeyExW, RegDeleteValueW, RegGetValueW, RegSetValueExW, HKEY,
    HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, HKEY_USERS, KEY_SET_VALUE, REG_OPTION_NON_VOLATILE,
    REG_SZ, RRF_RT_REG_SZ,
};
use windows::Win32::System::SystemInformation::GetSystemTime;
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const POLICY_KEY: &str = r"Software\Policies\Microsoft\Windows NT\Terminal Services";
const POLICY_VALUE: &str = "TrustedCertThumbprints";
/// A signer this close to expiry is replaced rather than used.
const EXPIRY_MARGIN_SECONDS: i64 = 24 * 60 * 60;

/// The client's signing certificate.
pub struct Signer {
    pub der: Vec<u8>,
    /// The SHA-1 thumbprint, which is what rdpsign looks certificates up by (see `sign`).
    pub sha1_hex: String,
    pub sha256_hex: String,
}

/// An open certificate store, closed when dropped.
struct Store(HCERTSTORE);

impl Drop for Store {
    fn drop(&mut self) {
        // SAFETY: the handle came from CertOpenStore and is closed once.
        unsafe {
            let _ = CertCloseStore(Some(self.0), 0);
        }
    }
}

fn open_store(location: u32, name: &str, writable: bool) -> Result<Store, String> {
    let name = HSTRING::from(name);
    let mut flags = location;
    if !writable {
        flags |= CERT_STORE_READONLY_FLAG.0 | CERT_STORE_OPEN_EXISTING_FLAG.0;
    }
    // SAFETY: the store name outlives the call.
    unsafe {
        CertOpenStore(
            CERT_STORE_PROV_SYSTEM_W,
            CERT_QUERY_ENCODING_TYPE(0),
            None,
            CERT_OPEN_STORE_FLAGS(flags),
            Some(name.as_ptr().cast::<c_void>()),
        )
    }
    .map(Store)
    .map_err(|e| format!("The certificate store could not be opened: {}", e.message()))
}

/// The certificates in `store`, each duplicated so it stays valid after the enumeration moves on;
/// free each with `free_all`.
fn certificates(store: &Store) -> Vec<*mut CERT_CONTEXT> {
    let mut found = Vec::new();
    let mut previous: *mut CERT_CONTEXT = std::ptr::null_mut();
    loop {
        // SAFETY: `previous` is null or the context the last call returned, which this call frees.
        let next = unsafe {
            CertEnumCertificatesInStore(
                store.0,
                (!previous.is_null()).then_some(previous.cast_const()),
            )
        };
        if next.is_null() {
            return found;
        }
        // SAFETY: `next` is a valid context from the enumeration.
        found.push(unsafe { CertDuplicateCertificateContext(Some(next.cast_const())) });
        previous = next;
    }
}

fn free_all(contexts: Vec<*mut CERT_CONTEXT>) {
    for context in contexts {
        // SAFETY: each context was duplicated once by `certificates`.
        unsafe {
            let _ = CertFreeCertificateContext(Some(context.cast_const()));
        }
    }
}

fn der_of(context: *const CERT_CONTEXT) -> Vec<u8> {
    // SAFETY: a valid context holds cbCertEncoded bytes at pbCertEncoded.
    unsafe {
        std::slice::from_raw_parts((*context).pbCertEncoded, (*context).cbCertEncoded as usize)
            .to_vec()
    }
}

fn sha1_of(context: *const CERT_CONTEXT) -> Option<String> {
    let mut hash = [0u8; 20];
    let mut size = hash.len() as u32;
    // SAFETY: the buffer and its size are valid for the call.
    unsafe {
        CertGetCertificateContextProperty(
            context,
            CERT_HASH_PROP_ID,
            Some(hash.as_mut_ptr().cast::<c_void>()),
            &mut size,
        )
    }
    .ok()?;
    Some(hex::encode_upper(&hash[..size as usize]))
}

/// Whether this user can use the certificate's private key without being asked.
fn has_private_key(context: *const CERT_CONTEXT) -> bool {
    let mut key = HCRYPTPROV_OR_NCRYPT_KEY_HANDLE::default();
    let mut caller_frees = BOOL(0);
    // SAFETY: out-parameters are valid; a key handle the caller must free is freed once.
    unsafe {
        let acquired = CryptAcquireCertificatePrivateKey(
            context,
            CRYPT_ACQUIRE_FLAGS(CRYPT_ACQUIRE_SILENT_FLAG.0 | CRYPT_ACQUIRE_ONLY_NCRYPT_KEY_FLAG.0),
            None,
            &mut key,
            None,
            Some(&mut caller_frees),
        )
        .is_ok();
        if acquired && caller_frees.as_bool() {
            let _ = NCryptFreeObject(NCRYPT_HANDLE(key.0));
        }
        acquired
    }
}

fn now_unix() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// The newest usable signer in the user's store: one of the client's own, not about to expire,
/// with its private key.
pub fn find_signer() -> Option<Signer> {
    let store = open_store(CERT_SYSTEM_STORE_CURRENT_USER, "MY", false).ok()?;
    let contexts = certificates(&store);
    let deadline = now_unix() + EXPIRY_MARGIN_SECONDS;
    let best = contexts
        .iter()
        .filter_map(|&context| {
            let der = der_of(context);
            let expiry = signer_expiry(&der)?;
            (expiry > deadline && has_private_key(context)).then_some((expiry, context, der))
        })
        .max_by_key(|(expiry, _, _)| *expiry)
        .and_then(|(_, context, der)| {
            Some(Signer {
                sha1_hex: sha1_of(context)?,
                sha256_hex: sha256_hex(&der),
                der,
            })
        });
    free_all(contexts);
    best
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

fn blob(bytes: &[u8]) -> CRYPT_INTEGER_BLOB {
    CRYPT_INTEGER_BLOB {
        cbData: bytes.len() as u32,
        pbData: bytes.as_ptr().cast_mut(),
    }
}

/// Makes a new signing certificate in the user's store: a 2048-bit RSA key in the Microsoft Software
/// Key Storage Provider that cannot be exported and may only sign, and a self-signed certificate
/// named `SIGNER_NAME` for Server Authentication only, not a certificate authority, valid for
/// `SIGNER_YEARS` years.
pub fn create_signer() -> Result<Signer, String> {
    let mut suffix = [0u8; 8];
    getrandom::fill(&mut suffix).map_err(|e| e.to_string())?;
    let key_name = format!("{SIGNER_NAME} {}", hex::encode(suffix));
    let failed = |step: &str, e: windows::core::Error| {
        format!(
            "The signing certificate could not be made ({step}): {}",
            e.message()
        )
    };

    let mut provider = NCRYPT_PROV_HANDLE::default();
    let mut key = NCRYPT_KEY_HANDLE::default();
    // SAFETY: each handle is created once and freed once below; every buffer outlives its call.
    unsafe {
        NCryptOpenStorageProvider(&mut provider, MS_KEY_STORAGE_PROVIDER, 0)
            .map_err(|e| failed("key storage", e))?;
        let created = NCryptCreatePersistedKey(
            provider,
            &mut key,
            NCRYPT_RSA_ALGORITHM,
            &HSTRING::from(key_name.as_str()),
            CERT_KEY_SPEC(0),
            NCRYPT_FLAGS(0),
        );
        let _ = NCryptFreeObject(NCRYPT_HANDLE(provider.0));
        created.map_err(|e| failed("key", e))?;

        let outcome = make_certificate(key, &key_name);
        if outcome.is_err() {
            // NCryptDeleteKey also frees the handle.
            let _ = NCryptDeleteKey(key, NCRYPT_SILENT_FLAG.0);
        } else {
            let _ = NCryptFreeObject(NCRYPT_HANDLE(key.0));
        }
        outcome?;
    }
    find_signer().ok_or_else(|| "The new signing certificate could not be found.".to_string())
}

/// Finalizes `key` and stores a self-signed certificate for it in the user's store.
///
/// # Safety
/// `key` must be a persisted key that is not yet finalized.
unsafe fn make_certificate(key: NCRYPT_KEY_HANDLE, key_name: &str) -> Result<(), String> {
    let failed = |step: &str, e: windows::core::Error| {
        format!(
            "The signing certificate could not be made ({step}): {}",
            e.message()
        )
    };
    let handle = NCRYPT_HANDLE(key.0);
    // SAFETY (whole body): the caller passes an unfinalized persisted key; every buffer and blob
    // below outlives the call that reads it.
    unsafe {
        NCryptSetProperty(
            handle,
            NCRYPT_LENGTH_PROPERTY,
            &2048u32.to_le_bytes(),
            NCRYPT_FLAGS(0),
        )
        .map_err(|e| failed("key length", e))?;
        // 0: the private key can never be exported.
        NCryptSetProperty(
            handle,
            NCRYPT_EXPORT_POLICY_PROPERTY,
            &0u32.to_le_bytes(),
            NCRYPT_FLAGS(0),
        )
        .map_err(|e| failed("export policy", e))?;
        NCryptSetProperty(
            handle,
            NCRYPT_KEY_USAGE_PROPERTY,
            &NCRYPT_ALLOW_SIGNING_FLAG.to_le_bytes(),
            NCRYPT_FLAGS(0),
        )
        .map_err(|e| failed("key usage", e))?;
        NCryptFinalizeKey(key, NCRYPT_FLAGS(0)).map_err(|e| failed("finalize", e))?;

        let subject = super::name_der(SIGNER_NAME);
        let subject_blob = blob(&subject);
        let extension = |oid: windows::core::PCSTR, critical: bool, value: &[u8]| CERT_EXTENSION {
            pszObjId: PSTR(oid.0.cast_mut()),
            fCritical: BOOL::from(critical),
            Value: blob(value),
        };
        let mut extensions = [
            extension(
                szOID_BASIC_CONSTRAINTS2,
                true,
                &BASIC_CONSTRAINTS_END_ENTITY,
            ),
            extension(szOID_ENHANCED_KEY_USAGE, false, &EKU_SERVER_AUTH),
            extension(szOID_KEY_USAGE, true, &KEY_USAGE_SIGNING),
        ];
        let extensions = CERT_EXTENSIONS {
            cExtension: extensions.len() as u32,
            rgExtension: extensions.as_mut_ptr(),
        };
        let algorithm = CRYPT_ALGORITHM_IDENTIFIER {
            pszObjId: PSTR(szOID_RSA_SHA256RSA.0.cast_mut()),
            Parameters: CRYPT_INTEGER_BLOB::default(),
        };
        let mut container = wide(key_name);
        let mut provider_name = wide("Microsoft Software Key Storage Provider");
        // Provider type 0 marks a CNG key; rdpsign and mstsc find it through this property.
        let provider_info = CRYPT_KEY_PROV_INFO {
            pwszContainerName: PWSTR(container.as_mut_ptr()),
            pwszProvName: PWSTR(provider_name.as_mut_ptr()),
            dwProvType: 0,
            dwFlags: CRYPT_KEY_FLAGS(0),
            cProvParam: 0,
            rgProvParam: std::ptr::null_mut(),
            dwKeySpec: 0,
        };
        let mut end = GetSystemTime();
        end.wYear += SIGNER_YEARS;
        if end.wMonth == 2 && end.wDay == 29 {
            end.wDay = 28;
        }
        let context = CertCreateSelfSignCertificate(
            Some(HCRYPTPROV_OR_NCRYPT_KEY_HANDLE(key.0)),
            &subject_blob,
            CERT_CREATE_SELFSIGN_FLAGS(0),
            Some(&provider_info),
            Some(&algorithm),
            None,
            Some(&end),
            Some(&extensions),
        );
        if context.is_null() {
            return Err(failed("certificate", windows::core::Error::from_thread()));
        }
        let friendly = wide(SIGNER_NAME);
        let friendly_blob = blob(std::slice::from_raw_parts(
            friendly.as_ptr().cast::<u8>(),
            friendly.len() * 2,
        ));
        let _ = CertSetCertificateContextProperty(
            context,
            CERT_FRIENDLY_NAME_PROP_ID,
            0,
            Some((&friendly_blob as *const CRYPT_INTEGER_BLOB).cast::<c_void>()),
        );
        let stored = open_store(CERT_SYSTEM_STORE_CURRENT_USER, "MY", true).and_then(|store| {
            CertAddCertificateContextToStore(Some(store.0), context, CERT_STORE_ADD_NEW, None)
                .map_err(|e| failed("store", e))
        });
        let _ = CertFreeCertificateContext(Some(context.cast_const()));
        stored
    }
}

/// Removes every one of the client's signing certificates from the user's store, with their keys.
pub fn delete_signers() -> Result<(), String> {
    let store = open_store(CERT_SYSTEM_STORE_CURRENT_USER, "MY", true)?;
    let contexts = certificates(&store);
    let mut outcome = Ok(());
    let mut others = Vec::new();
    for context in contexts {
        if signer_expiry(&der_of(context)).is_none() {
            others.push(context);
            continue;
        }
        // SAFETY: the context is valid; CertDeleteCertificateFromStore frees it, so it is not
        // freed again. The key handle from the acquire is freed by NCryptDeleteKey.
        unsafe {
            let mut key = HCRYPTPROV_OR_NCRYPT_KEY_HANDLE::default();
            let mut caller_frees = BOOL(0);
            if CryptAcquireCertificatePrivateKey(
                context,
                CRYPT_ACQUIRE_FLAGS(
                    CRYPT_ACQUIRE_SILENT_FLAG.0 | CRYPT_ACQUIRE_ONLY_NCRYPT_KEY_FLAG.0,
                ),
                None,
                &mut key,
                None,
                Some(&mut caller_frees),
            )
            .is_ok()
                && caller_frees.as_bool()
            {
                let _ = NCryptDeleteKey(NCRYPT_KEY_HANDLE(key.0), NCRYPT_SILENT_FLAG.0);
            }
            if let Err(e) = CertDeleteCertificateFromStore(context.cast_const()) {
                outcome = Err(format!(
                    "A signing certificate could not be removed: {}",
                    e.message()
                ));
            }
        }
    }
    free_all(others);
    outcome
}

/// The SHA-256 (hex) of each certificate named `SIGNER_NAME` in the machine's Trusted Root store.
fn root_signers() -> Result<Vec<String>, String> {
    let store = open_store(CERT_SYSTEM_STORE_LOCAL_MACHINE, "ROOT", false)?;
    let contexts = certificates(&store);
    let found = contexts
        .iter()
        .map(|&context| der_of(context))
        .filter(|der| signer_expiry(der).is_some())
        .map(|der| sha256_hex(&der))
        .collect();
    free_all(contexts);
    Ok(found)
}

fn read_string(root: HKEY, subkey: &str, value: &str) -> Option<String> {
    let (subkey, value) = (HSTRING::from(subkey), HSTRING::from(value));
    let mut size = 0u32;
    // SAFETY: the first call asks for the size; the second reads into a buffer of that size.
    unsafe {
        let status = RegGetValueW(
            root,
            &subkey,
            &value,
            RRF_RT_REG_SZ,
            None,
            None,
            Some(&mut size),
        );
        if status.0 != 0 || size < 2 {
            return None;
        }
        let mut buffer = vec![0u16; (size as usize).div_ceil(2)];
        let status = RegGetValueW(
            root,
            &subkey,
            &value,
            RRF_RT_REG_SZ,
            None,
            Some(buffer.as_mut_ptr().cast::<c_void>()),
            Some(&mut size),
        );
        if status.0 != 0 {
            return None;
        }
        let length = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
        Some(String::from_utf16_lossy(&buffer[..length]))
    }
}

/// Whether the user's or the machine's trusted publishers list names the certificate.
fn policy_names(sha256_hex: &str) -> bool {
    [HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE]
        .into_iter()
        .any(|root| {
            read_string(root, POLICY_KEY, POLICY_VALUE)
                .is_some_and(|list| policy_lists(&list, sha256_hex))
        })
}

fn rdpsign_path() -> PathBuf {
    std::env::var_os("SystemRoot")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(r"C:\Windows"))
        .join("System32")
        .join("rdpsign.exe")
}

/// Whether this device signs its .rdp files with a certificate Remote Desktop trusts.
pub fn status() -> TrustStatus {
    status_with(find_signer().as_ref())
}

fn status_with(signer: Option<&Signer>) -> TrustStatus {
    let mut facts = TrustFacts {
        rdpsign_available: rdpsign_path().is_file(),
        has_signer: signer.is_some(),
        ..TrustFacts::default()
    };
    if let Some(signer) = signer {
        facts.in_root = root_signers()
            .map(|roots| {
                roots
                    .iter()
                    .any(|r| r.eq_ignore_ascii_case(&signer.sha256_hex))
            })
            .unwrap_or(false);
        facts.listed = policy_names(&signer.sha256_hex);
    }
    decide(facts)
}

/// Signs the .rdp file at `file` in place when this device is set up for it. Returns whether it
/// signed; a file that is not signed still opens, with Windows' security dialog.
///
/// rdpsign rewrites the file as UTF-16 with "\r\r\n" line ends, which mstsc reads (verified with a
/// trusted signer, 2026-10-22). Its only certificate option is "/sha256 <hash>", yet on build 26100
/// it finds a certificate in the user's store only by the SHA-1 thumbprint there and answers the
/// SHA-256 with 0x80092004 (not found), so both are tried, SHA-1 first.
pub fn sign(file: &Path) -> Result<bool, String> {
    let Some(signer) = find_signer() else {
        return Ok(false);
    };
    if status_with(Some(&signer)) != TrustStatus::Ready {
        return Ok(false);
    }
    let mut last = String::new();
    for thumbprint in [&signer.sha1_hex, &signer.sha256_hex] {
        let output = Command::new(rdpsign_path())
            .args(["/sha256", thumbprint.as_str(), "/q"])
            .arg(file)
            .creation_flags(CREATE_NO_WINDOW)
            .output()
            .map_err(|e| format!("rdpsign could not be started: {e}"))?;
        if output.status.success() {
            return Ok(true);
        }
        last = String::from_utf8_lossy(&output.stdout).trim().to_string();
    }
    Err(format!(
        "rdpsign could not sign the connection file: {last}"
    ))
}

/// The string SID of the user this process runs as.
pub fn current_user_sid() -> Result<String, String> {
    let failed =
        |e: windows::core::Error| format!("The account could not be read: {}", e.message());
    // SAFETY: the token handle is closed once; the buffer is sized by the first call and holds a
    // TOKEN_USER whose SID points into it; the string the conversion allocates is freed once.
    unsafe {
        let mut token = HANDLE::default();
        OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).map_err(failed)?;
        let mut size = 0u32;
        let _ = GetTokenInformation(token, TokenUser, None, 0, &mut size);
        let mut buffer = vec![0u64; (size as usize).div_ceil(8)];
        let read = GetTokenInformation(
            token,
            TokenUser,
            Some(buffer.as_mut_ptr().cast::<c_void>()),
            size,
            &mut size,
        );
        let _ = CloseHandle(token);
        read.map_err(failed)?;
        let user = &*buffer.as_ptr().cast::<TOKEN_USER>();
        let mut text = PWSTR::null();
        ConvertSidToStringSidW(user.User.Sid, &mut text).map_err(failed)?;
        let sid = text.to_string().map_err(|e| e.to_string());
        let _ = LocalFree(Some(HLOCAL(text.0.cast::<c_void>())));
        sid
    }
}

/// The machine changes the elevated copy makes.
pub struct WindowsMachine;

impl WindowsMachine {
    fn policy_key(sid: &str) -> String {
        format!(r"{sid}\{POLICY_KEY}")
    }
}

impl MachineTrust for WindowsMachine {
    fn read_policy(&self, sid: &str) -> Result<String, String> {
        Ok(read_string(HKEY_USERS, &Self::policy_key(sid), POLICY_VALUE).unwrap_or_default())
    }

    fn write_policy(&self, sid: &str, list: &str) -> Result<(), String> {
        let subkey = HSTRING::from(Self::policy_key(sid));
        let mut key = HKEY::default();
        // SAFETY: the key handle is created once and closed once; the data outlives the call.
        unsafe {
            let status = RegCreateKeyExW(
                HKEY_USERS,
                &subkey,
                None,
                PCWSTR::null(),
                REG_OPTION_NON_VOLATILE,
                KEY_SET_VALUE,
                None,
                &mut key,
                None,
            );
            if status.0 != 0 {
                return Err(format!(
                    "The Remote Desktop policy could not be opened (error {}).",
                    status.0
                ));
            }
            let name = HSTRING::from(POLICY_VALUE);
            let status = if list.is_empty() {
                let status = RegDeleteValueW(key, &name);
                // 2: the value was not there.
                if status.0 == 2 {
                    windows::Win32::Foundation::WIN32_ERROR(0)
                } else {
                    status
                }
            } else {
                let data = wide(list);
                let bytes = std::slice::from_raw_parts(data.as_ptr().cast::<u8>(), data.len() * 2);
                RegSetValueExW(key, &name, None, REG_SZ, Some(bytes))
            };
            let _ = RegCloseKey(key);
            if status.0 != 0 {
                return Err(format!(
                    "The Remote Desktop policy could not be written (error {}).",
                    status.0
                ));
            }
        }
        Ok(())
    }

    fn root_signers(&self) -> Result<Vec<String>, String> {
        root_signers()
    }

    fn add_root(&self, der: &[u8]) -> Result<(), String> {
        let store = open_store(CERT_SYSTEM_STORE_LOCAL_MACHINE, "ROOT", true)?;
        // SAFETY: the store is open and the bytes outlive the call.
        unsafe {
            CertAddEncodedCertificateToStore(
                Some(store.0),
                CERT_QUERY_ENCODING_TYPE(X509_ASN_ENCODING.0 | PKCS_7_ASN_ENCODING.0),
                der,
                CERT_STORE_ADD_REPLACE_EXISTING,
                None,
            )
        }
        .map_err(|e| {
            format!(
                "The signing certificate could not be added to the trusted roots: {}",
                e.message()
            )
        })
    }

    fn remove_root(&self, sha256: &str) -> Result<(), String> {
        let store = open_store(CERT_SYSTEM_STORE_LOCAL_MACHINE, "ROOT", true)?;
        let contexts = certificates(&store);
        let mut outcome = Ok(());
        let mut others = Vec::new();
        for context in contexts {
            let der = der_of(context);
            if signer_expiry(&der).is_none() || !sha256_hex(&der).eq_ignore_ascii_case(sha256) {
                others.push(context);
                continue;
            }
            // SAFETY: CertDeleteCertificateFromStore frees the context, so it is not freed again.
            if let Err(e) = unsafe { CertDeleteCertificateFromStore(context.cast_const()) } {
                outcome = Err(format!(
                    "An earlier signing certificate could not be removed from the trusted roots: {}",
                    e.message()
                ));
            }
        }
        free_all(others);
        outcome
    }
}

/// Runs a trust command in this (elevated) process and writes the outcome to its result file.
/// Returns the process exit code.
pub fn run_helper(command: &TrustCommand) -> i32 {
    let (outcome, result) = match command {
        TrustCommand::Trust {
            certificate,
            sha256,
            sid,
            result,
        } => (
            std::fs::read(certificate)
                .map_err(|e| format!("The signing certificate could not be read: {e}"))
                .and_then(|der| super::trust(&WindowsMachine, &der, sha256, sid)),
            result,
        ),
        TrustCommand::Untrust { sid, result } => (super::untrust(&WindowsMachine, sid), result),
    };
    crate::elevated::write_result(result, &outcome);
    i32::from(outcome.is_err())
}

/// Makes a signer if there is none, then trusts it in an elevated copy of the client (one UAC
/// prompt). Returns the status afterwards.
pub fn set_up() -> Result<TrustStatus, String> {
    if !rdpsign_path().is_file() {
        return Ok(TrustStatus::Unsupported);
    }
    let signer = match find_signer() {
        Some(signer) => signer,
        None => create_signer()?,
    };
    if status_with(Some(&signer)) == TrustStatus::Ready {
        return Ok(TrustStatus::Ready);
    }
    let certificate = crate::elevated::result_file("rdp-signer").with_extension("cer");
    std::fs::write(&certificate, &signer.der)
        .map_err(|e| format!("The signing certificate could not be written: {e}"))?;
    let result = crate::elevated::result_file("rdp-trust");
    let outcome = crate::elevated::run(
        &[
            "--trust-rdp-signer".to_string(),
            certificate.display().to_string(),
            signer.sha256_hex.clone(),
            current_user_sid()?,
            result.display().to_string(),
        ],
        &result,
    );
    let _ = std::fs::remove_file(&certificate);
    outcome?;
    Ok(status())
}

/// Stops trusting this user's signers (one UAC prompt), then deletes them from the user's store.
pub fn remove() -> Result<TrustStatus, String> {
    let result = crate::elevated::result_file("rdp-untrust");
    crate::elevated::run(
        &[
            "--untrust-rdp-signer".to_string(),
            current_user_sid()?,
            result.display().to_string(),
        ],
        &result,
    )?;
    delete_signers()?;
    Ok(status())
}

#[cfg(test)]
mod live {
    use super::*;

    /// Makes a real signer in this user's store, checks it the way the elevated copy will, signs a
    /// file with rdpsign, and deletes it again. Needs no administrator rights; leaves a signer that
    /// was there before alone only if none existed (it deletes every HyperHarbor signer at the end).
    /// HH_RDP_TRUST_LIVE=1 cargo test --lib rdp_trust::system::live -- --ignored --nocapture
    #[test]
    #[ignore = "changes the user's certificate store"]
    fn a_new_signer_passes_the_elevated_checks_and_signs_a_file() {
        if std::env::var("HH_RDP_TRUST_LIVE").as_deref() != Ok("1") {
            return;
        }
        let signer = create_signer().unwrap();
        assert_eq!(super::super::check_signer(&signer.der), Ok(()));
        assert_eq!(signer.sha256_hex, sha256_hex(&signer.der));

        let store = open_store(CERT_SYSTEM_STORE_CURRENT_USER, "MY", false).unwrap();
        let contexts = certificates(&store);
        let ours = contexts
            .iter()
            .find(|&&c| der_of(c) == signer.der)
            .copied()
            .unwrap();
        // The key cannot be exported: NCRYPT_EXPORT_POLICY_PROPERTY reads back as 0.
        let mut key = HCRYPTPROV_OR_NCRYPT_KEY_HANDLE::default();
        let mut caller_frees = BOOL(0);
        let mut policy = [0u8; 4];
        let mut size = 0u32;
        unsafe {
            CryptAcquireCertificatePrivateKey(
                ours,
                CRYPT_ACQUIRE_FLAGS(
                    CRYPT_ACQUIRE_SILENT_FLAG.0 | CRYPT_ACQUIRE_ONLY_NCRYPT_KEY_FLAG.0,
                ),
                None,
                &mut key,
                None,
                Some(&mut caller_frees),
            )
            .unwrap();
            windows::Win32::Security::Cryptography::NCryptGetProperty(
                NCRYPT_HANDLE(key.0),
                NCRYPT_EXPORT_POLICY_PROPERTY,
                Some(&mut policy),
                &mut size,
                windows::Win32::Security::OBJECT_SECURITY_INFORMATION(0),
            )
            .unwrap();
            if caller_frees.as_bool() {
                let _ = NCryptFreeObject(NCRYPT_HANDLE(key.0));
            }
        }
        free_all(contexts);
        assert_eq!(u32::from_le_bytes(policy), 0);

        let file = std::env::temp_dir().join(format!("hh-rdp-trust-{}.rdp", std::process::id()));
        std::fs::write(
            &file,
            "full address:s:192.0.2.10:3389\r\nredirectclipboard:i:1\r\n",
        )
        .unwrap();
        let output = Command::new(rdpsign_path())
            .args(["/sha256", signer.sha1_hex.as_str()])
            .arg(&file)
            .output()
            .unwrap();
        let signed = std::fs::read(&file).unwrap();
        let _ = std::fs::remove_file(&file);
        delete_signers().unwrap();

        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stdout)
        );
        let text = String::from_utf16_lossy(
            &signed[2..]
                .as_chunks::<2>()
                .0
                .iter()
                .map(|pair| u16::from_le_bytes(*pair))
                .collect::<Vec<_>>(),
        );
        assert!(text.contains("signscope:s:Full Address,Alternate Full Address"));
        assert!(text.contains("signature:s:"));
        assert!(find_signer().is_none(), "the signer was not deleted");
    }
}
