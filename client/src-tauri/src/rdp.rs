//! One-click Remote Desktop: a temporary TERMSRV credential, a generated .rdp file, mstsc, and
//! cleanup. The credential is written with CredWriteW (the same entry `cmdkey /generic:TERMSRV/...`
//! creates) so the password never appears on a process command line. It is session-scoped and
//! removed as soon as mstsc has opened the session.

use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
use std::path::{Path, PathBuf};
use std::process::{Child, Command};
use std::time::{Duration, Instant};

use serde::Deserialize;
use zeroize::Zeroize;

use crate::error::ClientError;

/// Marks credentials this app created, so leftovers can be removed after a crash.
pub const CREDENTIAL_COMMENT: &str = "HyperHarbor temporary credential";

/// How long to wait for mstsc to open the session before removing the credential anyway. Long
/// enough for the first-connection certificate prompt, which appears before the credential is used.
const SESSION_WAIT: Duration = Duration::from_secs(120);
const REACHABILITY_TIMEOUT: Duration = Duration::from_secs(2);

/// Mirrors the GuestOsFamily schema.
#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum GuestOs {
    #[default]
    Unknown,
    Windows,
    Linux,
}

/// Mirrors the VmConnection schema. Debug never prints the password.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VmConnection {
    pub user_name: String,
    pub password: String,
    pub address: String,
    pub port: u16,
    #[serde(default)]
    pub guest_os: GuestOs,
}

impl std::fmt::Debug for VmConnection {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("VmConnection")
            .field("user_name", &self.user_name)
            .field("address", &self.address)
            .field("port", &self.port)
            .field("guest_os", &self.guest_os)
            .finish_non_exhaustive()
    }
}

impl Drop for VmConnection {
    fn drop(&mut self) {
        self.password.zeroize();
    }
}

/// The .rdp file for a connection. Signs in with the stored TERMSRV credential.
///
/// Windows guests use CredSSP (NLA) and get WebAuthn, microphone, and camera redirection. Linux
/// guests run xrdp, which does not support CredSSP or WebAuthn and camera redirection; with CredSSP
/// off, mstsc sends the stored credential in the TLS logon packet, which xrdp uses to sign in.
pub fn rdp_file(address: &str, port: u16, user_name: &str, guest_os: GuestOs) -> String {
    let linux = guest_os == GuestOs::Linux;
    let mut lines = vec![
        format!("full address:s:{address}:{port}"),
        format!("username:s:{user_name}"),
        "prompt for credentials:i:0".to_string(),
        "promptcredentialonce:i:0".to_string(),
        format!("enablecredsspsupport:i:{}", if linux { 0 } else { 1 }),
        "authentication level:i:2".to_string(),
        "audiomode:i:0".to_string(),
        "audiocapturemode:i:1".to_string(),
    ];
    if !linux {
        lines.push("redirectwebauthn:i:1".to_string());
        lines.push("camerastoredirect:s:*".to_string());
    }
    lines.join("\r\n") + "\r\n"
}

/// True when this device can open a TCP connection to the Remote Desktop port.
pub fn is_reachable(address: &str, port: u16) -> bool {
    let Ok(targets) = (address, port).to_socket_addrs() else {
        return false;
    };
    targets
        .collect::<Vec<SocketAddr>>()
        .iter()
        .any(|target| TcpStream::connect_timeout(target, REACHABILITY_TIMEOUT).is_ok())
}

/// Writes the credential, launches mstsc, and removes the credential and file in the background once
/// the session has opened (or mstsc exits, or the wait times out).
pub fn launch(connection: &VmConnection, file_directory: &Path) -> Result<(), ClientError> {
    let target = format!("TERMSRV/{}", connection.address);
    credentials::write(&target, &connection.user_name, &connection.password)?;

    let file = file_directory.join(format!(
        "hyperharbor-{}.rdp",
        connection.address.replace([':', '/', '\\'], "-")
    ));
    let started = std::fs::write(
        &file,
        rdp_file(
            &connection.address,
            connection.port,
            &connection.user_name,
            connection.guest_os,
        ),
    )
    .map_err(|e| ClientError::RdpFailed(e.to_string()))
    .and_then(|_| {
        Command::new("mstsc.exe")
            .arg(&file)
            .spawn()
            .map_err(|e| ClientError::RdpFailed(e.to_string()))
    });

    match started {
        Ok(child) => {
            let address = connection.address.clone();
            std::thread::spawn(move || {
                wait_for_session(child, &address);
                let _ = credentials::delete(&target);
                let _ = std::fs::remove_file(&file);
            });
            Ok(())
        }
        Err(error) => {
            let _ = credentials::delete(&target);
            let _ = std::fs::remove_file(&file);
            Err(error)
        }
    }
}

/// Removes credentials left behind by a previous run (for example, if the app was closed during a
/// connect). Returns how many were removed.
pub fn remove_stale_credentials() -> usize {
    credentials::remove_tagged("TERMSRV/*")
}

/// Returns when mstsc shows a session window for the address, exits, or the wait expires.
fn wait_for_session(mut child: Child, address: &str) {
    let started = Instant::now();
    while started.elapsed() < SESSION_WAIT {
        if matches!(child.try_wait(), Ok(Some(_))) {
            return;
        }
        if windows::has_window_title_containing(child.id(), address) {
            return;
        }
        std::thread::sleep(Duration::from_millis(500));
    }
}

/// Default directory for the generated .rdp files.
pub fn file_directory() -> PathBuf {
    std::env::temp_dir()
}

mod credentials {
    use windows_sys::Win32::Security::Credentials::{
        CredDeleteW, CredEnumerateW, CredFree, CredWriteW, CREDENTIALW, CRED_PERSIST_SESSION,
        CRED_TYPE_GENERIC,
    };

    use super::CREDENTIAL_COMMENT;
    use crate::error::ClientError;

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(std::iter::once(0)).collect()
    }

    pub fn write(target: &str, user_name: &str, password: &str) -> Result<(), ClientError> {
        let mut target = wide(target);
        let mut user = wide(user_name);
        let mut comment = wide(CREDENTIAL_COMMENT);
        // TERMSRV credentials store the password as UTF-16LE without a terminator.
        let mut blob: Vec<u8> = password.encode_utf16().flat_map(u16::to_le_bytes).collect();

        let credential = CREDENTIALW {
            Type: CRED_TYPE_GENERIC,
            TargetName: target.as_mut_ptr(),
            Comment: comment.as_mut_ptr(),
            CredentialBlobSize: blob.len() as u32,
            CredentialBlob: blob.as_mut_ptr(),
            Persist: CRED_PERSIST_SESSION,
            UserName: user.as_mut_ptr(),
            ..Default::default()
        };

        // SAFETY: every pointer refers to a buffer that outlives the call.
        let written = unsafe { CredWriteW(&credential, 0) } != 0;
        zeroize::Zeroize::zeroize(&mut blob);
        if written {
            Ok(())
        } else {
            Err(ClientError::RdpFailed(format!(
                "could not store the Remote Desktop credential: {}",
                std::io::Error::last_os_error()
            )))
        }
    }

    pub fn delete(target: &str) -> bool {
        let target = wide(target);
        // SAFETY: target is a valid null-terminated UTF-16 string.
        unsafe { CredDeleteW(target.as_ptr(), CRED_TYPE_GENERIC, 0) != 0 }
    }

    /// Deletes credentials matching the filter that carry this app's comment.
    pub fn remove_tagged(filter: &str) -> usize {
        let filter = wide(filter);
        let mut count = 0u32;
        let mut list: *mut *mut CREDENTIALW = std::ptr::null_mut();
        // SAFETY: on success CredEnumerateW returns `count` valid credential pointers, freed below.
        if unsafe { CredEnumerateW(filter.as_ptr(), 0, &mut count, &mut list) } == 0 {
            return 0;
        }

        let mut targets = Vec::new();
        for index in 0..count as usize {
            // SAFETY: index is within the returned array.
            let credential = unsafe { &**list.add(index) };
            if credential.Type == CRED_TYPE_GENERIC
                && unsafe { read_wide(credential.Comment) }.as_deref() == Some(CREDENTIAL_COMMENT)
            {
                if let Some(target) = unsafe { read_wide(credential.TargetName) } {
                    targets.push(target);
                }
            }
        }
        // SAFETY: list was allocated by CredEnumerateW.
        unsafe { CredFree(list.cast()) };

        targets.iter().filter(|target| delete(target)).count()
    }

    /// Reads a null-terminated UTF-16 string.
    unsafe fn read_wide(pointer: *const u16) -> Option<String> {
        if pointer.is_null() {
            return None;
        }
        let mut length = 0;
        while unsafe { *pointer.add(length) } != 0 {
            length += 1;
        }
        Some(String::from_utf16_lossy(unsafe {
            std::slice::from_raw_parts(pointer, length)
        }))
    }

    #[cfg(test)]
    pub fn exists(target: &str) -> bool {
        let filter = wide(target);
        let mut count = 0u32;
        let mut list: *mut *mut CREDENTIALW = std::ptr::null_mut();
        let found = unsafe { CredEnumerateW(filter.as_ptr(), 0, &mut count, &mut list) } != 0;
        if found {
            unsafe { CredFree(list.cast()) };
        }
        found && count > 0
    }
}

mod windows {
    use windows_sys::Win32::Foundation::{HWND, LPARAM};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        EnumWindows, GetWindowTextW, GetWindowThreadProcessId, IsWindowVisible,
    };

    struct Search<'a> {
        process_id: u32,
        needle: &'a str,
        found: bool,
    }

    unsafe extern "system" fn visit(window: HWND, state: LPARAM) -> i32 {
        // SAFETY: state is the &mut Search passed to EnumWindows below.
        let search = unsafe { &mut *(state as *mut Search) };
        let mut process_id = 0u32;
        unsafe { GetWindowThreadProcessId(window, &mut process_id) };
        if process_id == search.process_id && unsafe { IsWindowVisible(window) } != 0 {
            let mut buffer = [0u16; 512];
            let length =
                unsafe { GetWindowTextW(window, buffer.as_mut_ptr(), buffer.len() as i32) };
            let title = String::from_utf16_lossy(&buffer[..length.max(0) as usize]);
            if title.contains(search.needle) {
                search.found = true;
                return 0;
            }
        }
        1
    }

    /// True when a visible window of the process has a title containing `needle`. mstsc titles the
    /// session window "<address> - Remote Desktop Connection" once the connection is open.
    pub fn has_window_title_containing(process_id: u32, needle: &str) -> bool {
        let mut search = Search {
            process_id,
            needle,
            found: false,
        };
        // SAFETY: the callback only uses `search` for the duration of this call.
        unsafe { EnumWindows(Some(visit), &mut search as *mut Search as LPARAM) };
        search.found
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rdp_file_signs_in_and_enables_redirection() {
        let file = rdp_file("192.168.0.50", 3389, r".\hh-owner", GuestOs::Windows);
        let lines: Vec<&str> = file.lines().collect();

        for expected in [
            "full address:s:192.168.0.50:3389",
            r"username:s:.\hh-owner",
            "prompt for credentials:i:0",
            "redirectwebauthn:i:1",
            "audiocapturemode:i:1",
            "camerastoredirect:s:*",
        ] {
            assert!(lines.contains(&expected), "missing {expected}");
        }
        assert!(file.ends_with("\r\n"));
        assert!(!file.contains("password"));
    }

    #[test]
    fn rdp_file_for_linux_uses_tls_sign_in_without_windows_redirection() {
        let file = rdp_file("172.25.190.7", 3389, "hh-owner", GuestOs::Linux);
        let lines: Vec<&str> = file.lines().collect();

        assert!(lines.contains(&"username:s:hh-owner"));
        assert!(lines.contains(&"enablecredsspsupport:i:0"));
        assert!(lines.contains(&"audiocapturemode:i:1"));
        assert!(!file.contains("redirectwebauthn"));
        assert!(!file.contains("camerastoredirect"));
    }

    #[test]
    fn connection_reads_guest_os_from_json() {
        let connection: VmConnection = serde_json::from_str(
            r#"{"userName":"hh-owner","password":"x","address":"10.0.0.5","port":3389,
                "expiresAt":"2026-10-02T12:00:00Z","guestOs":"linux"}"#,
        )
        .unwrap();
        assert_eq!(connection.guest_os, GuestOs::Linux);
    }

    #[test]
    fn credential_write_tag_and_cleanup_round_trip() {
        let target = format!("TERMSRV/hyperharbor-test-{}", std::process::id());
        credentials::write(&target, r".\hh-owner", "Not-A-Real-Pass1!").unwrap();
        assert!(credentials::exists(&target));

        let removed =
            credentials::remove_tagged(&format!("TERMSRV/hyperharbor-test-{}", std::process::id()));
        assert_eq!(removed, 1);
        assert!(!credentials::exists(&target));
    }

    #[test]
    fn connection_debug_hides_password() {
        let connection = VmConnection {
            user_name: r".\hh-owner".into(),
            password: "Secret-Pass1!".into(),
            address: "192.168.0.50".into(),
            port: 3389,
            guest_os: GuestOs::Windows,
        };
        assert!(!format!("{connection:?}").contains("Secret-Pass1!"));
    }

    #[test]
    fn unreachable_address_is_detected() {
        // TEST-NET-1 (RFC 5737) is never routable.
        assert!(!is_reachable("192.0.2.1", 3389));
    }
}
