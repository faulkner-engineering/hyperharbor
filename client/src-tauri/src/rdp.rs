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
use crate::monitors::MonitorLayout;

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
    #[serde(default)]
    pub performance_mode: bool,
}

impl std::fmt::Debug for VmConnection {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("VmConnection")
            .field("user_name", &self.user_name)
            .field("address", &self.address)
            .field("port", &self.port)
            .field("guest_os", &self.guest_os)
            .field("performance_mode", &self.performance_mode)
            .finish_non_exhaustive()
    }
}

/// Longest user name Windows accepts for a local account, plus a ".\" prefix.
const MAX_USER_NAME: usize = 22;

impl VmConnection {
    /// Checks the values that go into the .rdp file and the TERMSRV credential target. The host
    /// sends an IP address and a generated account name; anything else (for example a line break,
    /// which would add arbitrary .rdp settings) is rejected rather than escaped.
    pub fn validate(&self) -> Result<(), ClientError> {
        if self.address.parse::<std::net::IpAddr>().is_err() {
            return Err(ClientError::InvalidResponse(
                "the Remote Desktop address is not an IP address".into(),
            ));
        }
        let user_name_ok = !self.user_name.is_empty()
            && self.user_name.chars().count() <= MAX_USER_NAME
            && self
                .user_name
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '\\' | '-' | '_'));
        if !user_name_ok {
            return Err(ClientError::InvalidResponse(
                "the Remote Desktop user name contains unexpected characters".into(),
            ));
        }
        if self.port == 0 {
            return Err(ClientError::InvalidResponse(
                "the Remote Desktop port is 0".into(),
            ));
        }
        Ok(())
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
///
/// In Performance mode the connection type is LAN with network and bandwidth auto-detection off, so
/// mstsc does not lower quality while it measures the link.
///
/// `monitors` chooses one monitor (mstsc's default), all of them, or the ones picked in the client.
pub fn rdp_file(
    address: &str,
    port: u16,
    user_name: &str,
    guest_os: GuestOs,
    performance_mode: bool,
    monitors: &MonitorLayout,
) -> String {
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
        // The guest resolution follows the window as it is resized, maximized, or moved to another
        // monitor, rather than the session being scaled. Servers without support (xrdp before 0.10)
        // keep their first resolution.
        "dynamic resolution:i:1".to_string(),
        "smart sizing:i:0".to_string(),
    ];
    if !linux {
        lines.push("redirectwebauthn:i:1".to_string());
        lines.push("camerastoredirect:s:*".to_string());
    }
    if performance_mode {
        lines.push("connection type:i:6".to_string());
        lines.push("networkautodetect:i:0".to_string());
        lines.push("bandwidthautodetect:i:0".to_string());
    }
    lines.extend(monitors.rdp_lines());
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

/// The .rdp file for a maintenance session on the host itself. No user name is set and no
/// credential is stored: mstsc asks for the host's Windows account (or uses one the user saved).
pub fn host_rdp_file(address: &str, port: u16, monitors: &MonitorLayout) -> String {
    let mut lines = vec![
        format!("full address:s:{}", rdp_endpoint(address, port)),
        "prompt for credentials:i:1".to_string(),
        "enablecredsspsupport:i:1".to_string(),
        "authentication level:i:2".to_string(),
        "audiomode:i:0".to_string(),
        "dynamic resolution:i:1".to_string(),
        "smart sizing:i:0".to_string(),
        "redirectclipboard:i:1".to_string(),
    ];
    lines.extend(monitors.rdp_lines());
    lines.join("\r\n") + "\r\n"
}

/// "address:port", with an IPv6 address in brackets.
fn rdp_endpoint(address: &str, port: u16) -> String {
    match address.parse::<std::net::IpAddr>() {
        Ok(std::net::IpAddr::V6(_)) => format!("[{address}]:{port}"),
        _ => format!("{address}:{port}"),
    }
}

/// True for an address that can go into an .rdp file: an IP address (IPv6 without a zone, which
/// mstsc does not accept) or a host name of letters, digits, dots, and hyphens.
pub fn is_valid_host_address(address: &str) -> bool {
    if let Ok(ip) = address.parse::<std::net::IpAddr>() {
        return !ip.is_unspecified();
    }
    !address.is_empty()
        && address.len() <= 253
        && !address.starts_with(['.', '-'])
        && address
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-'))
}

/// Opens Remote Desktop to the host for maintenance. Nothing is written to the credential store;
/// the .rdp file is removed once the session window opens.
pub fn launch_host(
    address: &str,
    port: u16,
    monitors: &MonitorLayout,
    file_directory: &Path,
) -> Result<(), ClientError> {
    if !is_valid_host_address(address) || port == 0 {
        return Err(ClientError::InvalidAddress);
    }
    start(
        Launch {
            credential: None,
            file_name: format!(
                "hyperharbor-host-{}.rdp",
                address.replace([':', '/', '\\', '%'], "-")
            ),
            contents: host_rdp_file(address, port, monitors),
            window_title_key: address.to_string(),
        },
        file_directory,
        None,
    )
}

/// A temporary credential for `TERMSRV/{host}`, removed once the session opens.
pub struct LaunchCredential<'a> {
    pub host: &'a str,
    pub user_name: &'a str,
    pub password: &'a str,
}

/// What to launch: an optional temporary credential, an .rdp file, and the text that appears in
/// the title of mstsc's session window once it has connected.
pub struct Launch<'a> {
    pub credential: Option<LaunchCredential<'a>>,
    pub file_name: String,
    pub contents: String,
    pub window_title_key: String,
}

/// Writes the credential, launches mstsc, and removes the credential and file in the background once
/// the session has opened (or mstsc exits, or the wait times out).
pub fn launch(
    connection: &VmConnection,
    monitors: &MonitorLayout,
    file_directory: &Path,
) -> Result<(), ClientError> {
    connection.validate()?;
    start(
        Launch {
            credential: Some(LaunchCredential {
                host: &connection.address,
                user_name: &connection.user_name,
                password: &connection.password,
            }),
            file_name: format!(
                "hyperharbor-{}.rdp",
                connection.address.replace([':', '/', '\\'], "-")
            ),
            contents: rdp_file(
                &connection.address,
                connection.port,
                &connection.user_name,
                connection.guest_os,
                connection.performance_mode,
                monitors,
            ),
            window_title_key: connection.address.clone(),
        },
        file_directory,
        None,
    )
}

/// Starts mstsc for `launch`. The credential and file are removed once the session has opened;
/// `on_exit`, if given, runs after mstsc exits.
pub fn start(
    launch: Launch<'_>,
    file_directory: &Path,
    on_exit: Option<Box<dyn FnOnce() + Send>>,
) -> Result<(), ClientError> {
    let target = match &launch.credential {
        Some(credential) => {
            let target = format!("TERMSRV/{}", credential.host);
            credentials::write(&target, credential.user_name, credential.password)?;
            Some(target)
        }
        None => None,
    };
    let remove_credential = move || {
        if let Some(target) = &target {
            let _ = credentials::delete(target);
        }
    };

    let file = file_directory.join(&launch.file_name);
    let started = std::fs::write(&file, &launch.contents)
        .map_err(|e| ClientError::RdpFailed(e.to_string()))
        .and_then(|_| {
            Command::new("mstsc.exe")
                .arg(&file)
                .spawn()
                .map_err(|e| ClientError::RdpFailed(e.to_string()))
        });

    match started {
        Ok(mut child) => {
            let key = launch.window_title_key;
            std::thread::spawn(move || {
                wait_for_session(&mut child, &key);
                remove_credential();
                let _ = std::fs::remove_file(&file);
                if let Some(on_exit) = on_exit {
                    let _ = child.wait();
                    on_exit();
                }
            });
            Ok(())
        }
        Err(error) => {
            remove_credential();
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

/// Returns when mstsc shows a session window whose title contains `title_key`, exits, or the wait expires.
fn wait_for_session(child: &mut Child, title_key: &str) {
    let started = Instant::now();
    while started.elapsed() < SESSION_WAIT {
        if matches!(child.try_wait(), Ok(Some(_))) {
            return;
        }
        if windows::has_window_title_containing(child.id(), title_key) {
            return;
        }
        std::thread::sleep(Duration::from_millis(500));
    }
}

/// Default directory for the generated .rdp files.
pub fn file_directory() -> PathBuf {
    std::env::temp_dir()
}

#[allow(unsafe_code)]
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

    /// Writes a session-scoped credential tagged with this app's comment.
    pub fn write(target: &str, user_name: &str, password: &str) -> Result<(), ClientError> {
        write_with_comment(target, user_name, password, Some(CREDENTIAL_COMMENT))
    }

    /// Writes a credential without the tag, like one the user created. For tests of cleanup.
    #[cfg(test)]
    pub fn write_untagged(
        target: &str,
        user_name: &str,
        password: &str,
    ) -> Result<(), ClientError> {
        write_with_comment(target, user_name, password, None)
    }

    fn write_with_comment(
        target: &str,
        user_name: &str,
        password: &str,
        comment: Option<&str>,
    ) -> Result<(), ClientError> {
        let mut target = wide(target);
        let mut user = wide(user_name);
        let mut comment = comment.map(wide);
        // TERMSRV credentials store the password as UTF-16LE without a terminator.
        let mut blob: Vec<u8> = password.encode_utf16().flat_map(u16::to_le_bytes).collect();

        let credential = CREDENTIALW {
            Type: CRED_TYPE_GENERIC,
            TargetName: target.as_mut_ptr(),
            Comment: comment
                .as_mut()
                .map_or(std::ptr::null_mut(), |comment| comment.as_mut_ptr()),
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

#[allow(unsafe_code)]
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
        let file = rdp_file(
            "192.168.0.50",
            3389,
            r".\hh-owner",
            GuestOs::Windows,
            false,
            &MonitorLayout::Single,
        );
        let lines: Vec<&str> = file.lines().collect();

        for expected in [
            "full address:s:192.168.0.50:3389",
            r"username:s:.\hh-owner",
            "prompt for credentials:i:0",
            "redirectwebauthn:i:1",
            "audiocapturemode:i:1",
            "camerastoredirect:s:*",
            "dynamic resolution:i:1",
            "smart sizing:i:0",
        ] {
            assert!(lines.contains(&expected), "missing {expected}");
        }
        assert!(file.ends_with("\r\n"));
        assert!(!file.contains("password"));
        assert!(!file.contains("connection type"));
        assert!(!file.contains("autodetect"));
        assert!(!file.contains("multimon"));
    }

    #[test]
    fn rdp_file_uses_the_chosen_monitors() {
        let file = rdp_file(
            "192.168.0.50",
            3389,
            "hh-owner",
            GuestOs::Windows,
            false,
            &MonitorLayout::Selected(vec![2, 0]),
        );
        let lines: Vec<&str> = file.lines().collect();

        assert!(lines.contains(&"use multimon:i:1"));
        assert!(lines.contains(&"selectedmonitors:s:2,0"));
        assert!(lines.contains(&"dynamic resolution:i:1"));
    }

    #[test]
    fn host_rdp_file_asks_for_credentials_and_names_no_user() {
        let file = host_rdp_file("192.168.0.10", 3389, &MonitorLayout::All);
        let lines: Vec<&str> = file.lines().collect();

        for expected in [
            "full address:s:192.168.0.10:3389",
            "prompt for credentials:i:1",
            "enablecredsspsupport:i:1",
            "authentication level:i:2",
            "use multimon:i:1",
        ] {
            assert!(lines.contains(&expected), "missing {expected}");
        }
        assert!(!file.contains("username"));
        assert!(!file.contains("password"));
        assert!(file.ends_with("\r\n"));
    }

    #[test]
    fn host_rdp_file_brackets_ipv6_addresses() {
        let file = host_rdp_file("fd00::10", 3390, &MonitorLayout::Single);

        assert!(file
            .lines()
            .any(|line| line == "full address:s:[fd00::10]:3390"));
    }

    #[test]
    fn host_addresses_must_be_ip_addresses_or_plain_host_names() {
        for valid in ["192.168.0.10", "fd00::10", "tc-pc", "tc-pc.local"] {
            assert!(is_valid_host_address(valid), "{valid}");
        }
        for invalid in [
            "",
            "0.0.0.0",
            "fe80::1%12",
            "host\r\nusername:s:x",
            "host:3389",
            "-host",
            "host name",
        ] {
            assert!(!is_valid_host_address(invalid), "{invalid:?}");
        }
    }

    #[test]
    fn rdp_file_in_performance_mode_uses_lan_without_auto_detection() {
        let file = rdp_file(
            "192.168.0.50",
            3389,
            "hh-owner",
            GuestOs::Windows,
            true,
            &MonitorLayout::Single,
        );
        let lines: Vec<&str> = file.lines().collect();

        for expected in [
            "connection type:i:6",
            "networkautodetect:i:0",
            "bandwidthautodetect:i:0",
        ] {
            assert!(lines.contains(&expected), "missing {expected}");
        }
    }

    #[test]
    fn rdp_file_for_linux_uses_tls_sign_in_without_windows_redirection() {
        let file = rdp_file(
            "172.25.190.7",
            3389,
            "hh-owner",
            GuestOs::Linux,
            false,
            &MonitorLayout::Single,
        );
        let lines: Vec<&str> = file.lines().collect();

        assert!(lines.contains(&"username:s:hh-owner"));
        assert!(lines.contains(&"enablecredsspsupport:i:0"));
        assert!(lines.contains(&"audiocapturemode:i:1"));
        assert!(lines.contains(&"dynamic resolution:i:1"));
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
        assert!(!connection.performance_mode);

        let tuned: VmConnection = serde_json::from_str(
            r#"{"userName":"hh-owner","password":"x","address":"10.0.0.5","port":3389,
                "expiresAt":"2026-10-02T12:00:00Z","performanceMode":true}"#,
        )
        .unwrap();
        assert!(tuned.performance_mode);
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
    fn cleanup_keeps_credentials_it_did_not_create() {
        let prefix = format!("TERMSRV/hyperharbor-test-untagged-{}", std::process::id());
        let ours = format!("{prefix}-ours");
        let users = format!("{prefix}-users");
        credentials::write(&ours, "hh-owner", "Not-A-Real-Pass1!").unwrap();
        credentials::write_untagged(&users, "someone", "Users-Own-Pass1!").unwrap();

        let removed = credentials::remove_tagged(&format!("{prefix}-*"));

        let users_survived = credentials::exists(&users);
        credentials::delete(&users);
        assert_eq!(removed, 1);
        assert!(!credentials::exists(&ours));
        assert!(
            users_survived,
            "cleanup removed a credential the user created"
        );
    }

    fn connection(address: &str, user_name: &str) -> VmConnection {
        VmConnection {
            user_name: user_name.into(),
            password: "Not-A-Real-Pass1!".into(),
            address: address.into(),
            port: 3389,
            guest_os: GuestOs::Windows,
            performance_mode: false,
        }
    }

    #[test]
    fn validate_accepts_what_the_host_sends() {
        for (address, user_name) in [
            ("192.168.0.50", r".\hh-owner"),
            ("172.25.190.7", "hh-owner"),
            ("fd00::5", "hh-a_b.c"),
        ] {
            assert!(
                connection(address, user_name).validate().is_ok(),
                "{address} {user_name}"
            );
        }
    }

    #[test]
    fn validate_rejects_values_that_could_change_the_rdp_file() {
        for (address, user_name) in [
            ("192.168.0.50\r\nalternate shell:s:cmd.exe", "hh-owner"),
            ("192.168.0.50", "hh-owner\r\ndrivestoredirect:s:*"),
            ("192.168.0.50", "hh-owner\ndrivestoredirect:s:*"),
            ("vm.example.com", "hh-owner"),
            ("192.168.0.50:3390", "hh-owner"),
            ("", "hh-owner"),
            ("192.168.0.50", ""),
            ("192.168.0.50", "hh owner"),
            ("192.168.0.50", "hh-owner;x"),
            ("192.168.0.50", "hh-ownerhh-ownerhh-owner"),
            ("192.168.0.50", "TERMSRV/*"),
        ] {
            assert!(
                matches!(
                    connection(address, user_name).validate(),
                    Err(ClientError::InvalidResponse(_))
                ),
                "accepted {address:?} {user_name:?}"
            );
        }
        let mut zero_port = connection("192.168.0.50", "hh-owner");
        zero_port.port = 0;
        assert!(zero_port.validate().is_err());
    }

    #[test]
    fn launch_with_injected_address_writes_nothing() {
        let directory =
            std::env::temp_dir().join(format!("hyperharbor-test-{}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        let address = "10.0.0.5\r\nalternate shell:s:cmd.exe";

        let result = launch(
            &connection(address, "hh-owner"),
            &MonitorLayout::Single,
            &directory,
        );

        let files = std::fs::read_dir(&directory).unwrap().count();
        let _ = std::fs::remove_dir_all(&directory);
        assert!(matches!(result, Err(ClientError::InvalidResponse(_))));
        assert!(!credentials::exists(&format!("TERMSRV/{address}")));
        assert_eq!(files, 0);
    }

    #[test]
    fn connection_debug_hides_password() {
        let connection = VmConnection {
            user_name: r".\hh-owner".into(),
            password: "Secret-Pass1!".into(),
            address: "192.168.0.50".into(),
            port: 3389,
            guest_os: GuestOs::Windows,
            performance_mode: false,
        };
        assert!(!format!("{connection:?}").contains("Secret-Pass1!"));
    }

    #[test]
    fn unreachable_address_is_detected() {
        // TEST-NET-1 (RFC 5737) is never routable.
        assert!(!is_reachable("192.0.2.1", 3389));
    }
}
