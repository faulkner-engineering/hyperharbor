//! The client's side of the signed-in session live tests (`SessionLiveTests` in Host.Tests). The
//! host test drives everything: it asks the live host harness for credentials, checks the guest,
//! and resizes and reads the session windows. This agent only opens Remote Desktop the way the
//! client's Connect does, with `rdp::launch`, so the tests see the client's real .rdp file.
//!
//! Ignored by default. `scripts\live-session.ps1` starts it with
//! `HH_SESSION_LIVE=1 HH_SESSION_DIR=<folder> cargo test --lib session_live -- --ignored --nocapture`.
//!
//! It watches the folder for `<id>.launch.json` requests: `connection` (a VmConnection without
//! its password), `protectedPassword` (the password protected with DPAPI for this Windows account
//! and `ENTROPY`, in base64, so no file ever holds it in plain text), `vmName`, and `plain` (true
//! opens a minimal .rdp file instead of the client's, for comparisons). Each request is deleted as
//! soon as it is read and answered in `<id>.launched.json` with `pid` (mstsc's process id) or
//! `error`. It stops when `done` appears in the folder, or after an hour.
#![cfg(test)]

use crate::camera::CameraRedirect;
use crate::monitors::MonitorLayout;
use crate::rdp;
use base64::Engine;
use serde::Deserialize;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

const LIFETIME: Duration = Duration::from_secs(3600);

/// Extra DPAPI entropy, the same as SessionLiveTests uses, so other data this account protected
/// cannot be passed off as a password here.
const ENTROPY: &[u8] = b"HyperHarbor session live test";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct LaunchRequest {
    connection: serde_json::Value,
    protected_password: String,
    vm_name: String,
    #[serde(default)]
    plain: bool,
}

impl LaunchRequest {
    /// The connection with its password opened from `protected_password`.
    fn connection(&self) -> Result<rdp::VmConnection, String> {
        let mut value = self.connection.clone();
        value["password"] = serde_json::Value::String(String::new());
        let mut connection: rdp::VmConnection =
            serde_json::from_value(value).map_err(|e| e.to_string())?;
        let protected = base64::engine::general_purpose::STANDARD
            .decode(&self.protected_password)
            .map_err(|e| e.to_string())?;
        connection.password = dpapi::unprotect(&protected)?;
        Ok(connection)
    }
}

/// DPAPI for the current Windows account (CryptProtectData and CryptUnprotectData).
#[allow(unsafe_code)]
mod dpapi {
    use super::ENTROPY;
    use windows::Win32::Foundation::{LocalFree, HLOCAL};
    use windows::Win32::Security::Cryptography::{
        CryptProtectData, CryptUnprotectData, CRYPT_INTEGER_BLOB,
    };

    fn blob(bytes: &[u8]) -> CRYPT_INTEGER_BLOB {
        CRYPT_INTEGER_BLOB {
            cbData: bytes.len() as u32,
            pbData: bytes.as_ptr().cast_mut(),
        }
    }

    /// Copies DPAPI's output and frees it.
    fn take(output: CRYPT_INTEGER_BLOB) -> Vec<u8> {
        // SAFETY: on success DPAPI returns cbData bytes at pbData, allocated with LocalAlloc.
        unsafe {
            let bytes = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
            let _ = LocalFree(Some(HLOCAL(output.pbData.cast())));
            bytes
        }
    }

    pub fn unprotect(data: &[u8]) -> Result<String, String> {
        let (input, entropy) = (blob(data), blob(ENTROPY));
        let mut output = CRYPT_INTEGER_BLOB::default();
        // SAFETY: the input blobs point at live slices for the duration of the call.
        unsafe { CryptUnprotectData(&input, None, Some(&entropy), None, None, 0, &mut output) }
            .map_err(|e| format!("The password could not be opened: {e}"))?;
        String::from_utf8(take(output)).map_err(|e| e.to_string())
    }

    /// For the round-trip test; SessionLiveTests protects with .NET's ProtectedData.
    #[cfg(test)]
    pub fn protect(text: &str) -> Vec<u8> {
        let (input, entropy) = (blob(text.as_bytes()), blob(ENTROPY));
        let mut output = CRYPT_INTEGER_BLOB::default();
        // SAFETY: as in unprotect.
        unsafe { CryptProtectData(&input, None, Some(&entropy), None, None, 0, &mut output) }
            .unwrap();
        take(output)
    }
}

/// The lines `mstsc /v:<address>` would use from a default profile, plus the sign-in lines the
/// temporary credential needs. Compared with the client's file, it tells whether a behavior comes
/// from the client's settings or from Remote Desktop itself.
fn plain_file(connection: &rdp::VmConnection) -> String {
    [
        format!("full address:s:{}:{}", connection.address, connection.port),
        format!("username:s:{}", connection.user_name),
        "prompt for credentials:i:0".to_string(),
        "screen mode id:i:1".to_string(),
    ]
    .join("\r\n")
        + "\r\n"
}

fn launch(request: &LaunchRequest) -> Result<u32, String> {
    let connection = &request.connection()?;
    if request.plain {
        connection.validate().map_err(|e| e.to_string())?;
        return rdp::start(
            rdp::Launch {
                credential: Some(rdp::LaunchCredential {
                    host: &connection.address,
                    user_name: &connection.user_name,
                    password: &connection.password,
                }),
                window_name: format!("{} (plain)", request.vm_name),
                contents: plain_file(connection),
                window_title_key: connection.address.clone(),
            },
            &rdp::file_directory(),
            None,
        )
        .map_err(|e| e.to_string());
    }

    // As Connect does on a device without a monitor choice and without a virtual camera.
    let layout = connection
        .defaults
        .as_ref()
        .map(rdp::ConnectionDefaults::monitor_layout)
        .unwrap_or(MonitorLayout::Single);
    rdp::launch(
        connection,
        Some(&request.vm_name),
        &layout,
        &CameraRedirect::AllPhysical,
        &rdp::file_directory(),
        None,
    )
    .map_err(|e| e.to_string())
}

/// Writes the answer under a temporary name first, so the host test never reads half a file.
fn answer(folder: &Path, id: &str, value: serde_json::Value) {
    answer_as(folder, &format!("{id}.launched.json"), value);
}

fn answer_as(folder: &Path, name: &str, value: serde_json::Value) {
    let partial = folder.join(format!("{name}.partial"));
    std::fs::write(&partial, value.to_string()).unwrap();
    std::fs::rename(&partial, folder.join(name)).unwrap();
}

fn pending(folder: &Path) -> Vec<(String, PathBuf)> {
    let Ok(entries) = std::fs::read_dir(folder) else {
        return Vec::new();
    };
    entries
        .filter_map(Result::ok)
        .filter_map(|entry| {
            let name = entry.file_name().to_string_lossy().into_owned();
            name.strip_suffix(".launch.json")
                .map(|id| (id.to_string(), entry.path()))
        })
        .collect()
}

#[test]
#[ignore = "opens Remote Desktop sessions for SessionLiveTests; set HH_SESSION_LIVE=1 and HH_SESSION_DIR"]
fn serve_session_launches() {
    if std::env::var("HH_SESSION_LIVE").is_ok_and(|v| v == "1") {
        let Ok(folder) = std::env::var("HH_SESSION_DIR") else {
            return;
        };
        let folder = PathBuf::from(folder);
        std::fs::create_dir_all(&folder).unwrap();
        println!("Serving launches from {}", folder.display());
        // Unsigned files stop at mstsc's security dialog, so the test reports it when trust is not set up.
        let trust = crate::rdp_trust::system::status();
        answer_as(&folder, "agent.json", serde_json::json!({ "trust": trust }));
        let started = Instant::now();
        while !folder.join("done").exists() && started.elapsed() < LIFETIME {
            for (id, path) in pending(&folder) {
                let text = std::fs::read(&path);
                let _ = std::fs::remove_file(&path);
                let result = text
                    .map_err(|e| e.to_string())
                    .and_then(|text| {
                        serde_json::from_slice::<LaunchRequest>(&text).map_err(|e| e.to_string())
                    })
                    .and_then(|request| {
                        println!(
                            "{id}: launching {} (plain: {})",
                            request.vm_name, request.plain
                        );
                        launch(&request)
                    });
                match result {
                    Ok(pid) => answer(&folder, &id, serde_json::json!({ "pid": pid })),
                    Err(error) => {
                        println!("{id}: {error}");
                        answer(&folder, &id, serde_json::json!({ "error": error }));
                    }
                }
            }
            std::thread::sleep(Duration::from_millis(250));
        }
    }
}

mod tests {
    use super::*;

    #[test]
    fn the_plain_file_signs_in_without_the_clients_settings() {
        let connection: rdp::VmConnection = serde_json::from_value(serde_json::json!({
            "userName": r".\hh-owner",
            "password": "secret",
            "address": "192.168.0.50",
            "port": 3389,
        }))
        .unwrap();
        let file = plain_file(&connection);

        assert!(file.starts_with("full address:s:192.168.0.50:3389\r\n"));
        assert!(file.contains("prompt for credentials:i:0"));
        assert!(!file.contains("dynamic resolution"));
        assert!(!file.contains("secret"));
    }

    #[test]
    fn a_request_carries_the_password_only_protected() {
        let protected = dpapi::protect("secret");
        assert!(!protected.windows(6).any(|part| part == b"secret"));
        let request: LaunchRequest = serde_json::from_value(serde_json::json!({
            "connection": {
                "userName": "hh-owner",
                "address": "192.168.0.50",
                "port": 3389,
            },
            "protectedPassword": base64::engine::general_purpose::STANDARD.encode(&protected),
            "vmName": "VM",
        }))
        .unwrap();

        assert_eq!(request.connection().unwrap().password, "secret");
    }
}
