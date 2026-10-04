//! VM console: Remote Desktop to the host's Virtual Machine Connection service (port 2179), which
//! shows the VM's video output before and after its OS starts. mstsc connects to a listener on this
//! device's loopback; every connection it opens is carried to the host through an upgraded,
//! certificate-pinned API request, so the console needs no other port and works from any paired
//! device. The listener closes when mstsc exits; tunnels already open run until either side closes.

use std::sync::Arc;

use serde::Deserialize;
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::oneshot;
use zeroize::{Zeroize, Zeroizing};

use crate::api::ApiClient;
use crate::error::ClientError;
use crate::hosts::HostEntry;
use crate::paired::PairedHost;
use crate::rdp;

/// The longest "HOST\account" name: a 15-character computer name, a backslash, and a 20-character account.
const MAX_USER_NAME: usize = 36;
const MAX_TICKET: usize = 64;

/// Mirrors the ConsoleSession schema. Debug never prints the ticket or password.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsoleSession {
    pub ticket: String,
    pub user_name: String,
    pub password: String,
    pub pcb: String,
}

impl std::fmt::Debug for ConsoleSession {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ConsoleSession")
            .field("user_name", &self.user_name)
            .field("pcb", &self.pcb)
            .finish_non_exhaustive()
    }
}

impl Drop for ConsoleSession {
    fn drop(&mut self) {
        self.ticket.zeroize();
        self.password.zeroize();
    }
}

impl ConsoleSession {
    /// Checks the values that go into the .rdp file, the TERMSRV credential, and the tunnel header.
    /// Anything unexpected (for example a line break, which would add .rdp settings) is rejected.
    pub fn validate(&self) -> Result<(), ClientError> {
        let user_name_ok = !self.user_name.is_empty()
            && self.user_name.chars().count() <= MAX_USER_NAME
            && self.user_name.matches('\\').count() == 1
            && self
                .user_name
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '\\' | '-' | '_'));
        if !user_name_ok {
            return Err(ClientError::InvalidResponse(
                "the console user name contains unexpected characters".into(),
            ));
        }
        if !crate::hosts::is_guid(&self.pcb) {
            return Err(ClientError::InvalidResponse(
                "the console pre-connection blob is not a VM ID".into(),
            ));
        }
        let ticket_ok = !self.ticket.is_empty()
            && self.ticket.len() <= MAX_TICKET
            && self
                .ticket
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_'));
        if !ticket_ok {
            return Err(ClientError::InvalidResponse(
                "the console ticket contains unexpected characters".into(),
            ));
        }
        Ok(())
    }
}

/// The .rdp file for a console session through the local listener on `port`.
///
/// The pre-connection blob selects the VM. VMMS starts TLS right after it, without the usual
/// security negotiation, so negotiation is off; with it on, mstsc stalls at "Configuring remote
/// session". Server authentication is off because VMMS presents a self-signed certificate, and the
/// tunnel has already authenticated the host by its pinned certificate; the far end of the tunnel is
/// the host's own loopback.
pub fn console_rdp_file(port: u16, user_name: &str, pcb: &str) -> String {
    [
        format!("full address:s:127.0.0.1:{port}"),
        format!("pcb:s:{pcb}"),
        "negotiate security layer:i:0".to_string(),
        format!("username:s:{user_name}"),
        "prompt for credentials:i:0".to_string(),
        "promptcredentialonce:i:0".to_string(),
        "enablecredsspsupport:i:1".to_string(),
        "authentication level:i:0".to_string(),
        "audiomode:i:2".to_string(),
    ]
    .join("\r\n")
        + "\r\n"
}

/// Opens the VM's console: asks the host for a session, starts the loopback listener, and launches
/// mstsc against it.
pub async fn open(
    api: Arc<ApiClient>,
    host: HostEntry,
    paired: PairedHost,
    vm_id: String,
) -> Result<(), ClientError> {
    let session = api.open_console(&host, &paired, &vm_id).await?;
    session.validate()?;

    let listener = TcpListener::bind(("127.0.0.1", 0))
        .await
        .map_err(|e| ClientError::RdpFailed(format!("could not listen on loopback: {e}")))?;
    let port = listener
        .local_addr()
        .map_err(|e| ClientError::RdpFailed(e.to_string()))?
        .port();

    let (stop, stopped) = oneshot::channel::<()>();
    let ticket = Zeroizing::new(session.ticket.clone());
    tauri::async_runtime::spawn(serve(listener, stopped, api, host, paired, vm_id, ticket));

    // If mstsc fails to start, `stop` is dropped here, which also ends the listener.
    rdp::start(
        rdp::Launch {
            credential_host: "127.0.0.1",
            user_name: &session.user_name,
            password: &session.password,
            file_name: format!("hyperharbor-console-{port}.rdp"),
            contents: console_rdp_file(port, &session.user_name, &session.pcb),
            window_title_key: format!("127.0.0.1:{port}"),
        },
        &rdp::file_directory(),
        Some(Box::new(move || {
            let _ = stop.send(());
        })),
    )
}

/// Accepts mstsc's connections until `stopped` fires (or its sender is dropped), tunneling each one.
pub(crate) async fn serve(
    listener: TcpListener,
    mut stopped: oneshot::Receiver<()>,
    api: Arc<ApiClient>,
    host: HostEntry,
    paired: PairedHost,
    vm_id: String,
    ticket: Zeroizing<String>,
) {
    let ticket = Arc::new(ticket);
    loop {
        tokio::select! {
            _ = &mut stopped => break,
            accepted = listener.accept() => {
                let Ok((stream, _)) = accepted else { break };
                tokio::spawn(tunnel(
                    stream,
                    api.clone(),
                    host.clone(),
                    paired.clone(),
                    vm_id.clone(),
                    ticket.clone(),
                ));
            }
        }
    }
}

/// Carries one local connection to the host's console service until either side closes. A failed
/// tunnel closes the local connection, which mstsc reports as a connection error.
async fn tunnel(
    mut stream: TcpStream,
    api: Arc<ApiClient>,
    host: HostEntry,
    paired: PairedHost,
    vm_id: String,
    ticket: Arc<Zeroizing<String>>,
) {
    let _ = stream.set_nodelay(true);
    match api
        .open_console_tunnel(&host, &paired, &vm_id, ticket.as_str())
        .await
    {
        Ok(mut upgraded) => {
            let _ = tokio::io::copy_bidirectional(&mut stream, &mut upgraded).await;
        }
        Err(error) => eprintln!("The console tunnel could not be opened: {error}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const VM_ID: &str = "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b";

    fn session(user_name: &str, pcb: &str, ticket: &str) -> ConsoleSession {
        ConsoleSession {
            ticket: ticket.into(),
            user_name: user_name.into(),
            password: "Console-Secret1!".into(),
            pcb: pcb.into(),
        }
    }

    #[test]
    fn rdp_file_selects_the_vm_through_the_local_listener() {
        let file = console_rdp_file(50123, r"HOSTPC\hhc-owner", VM_ID);
        let lines: Vec<&str> = file.lines().collect();

        assert!(lines.contains(&"full address:s:127.0.0.1:50123"));
        assert!(lines.contains(&format!("pcb:s:{VM_ID}").as_str()));
        assert!(lines.contains(&"negotiate security layer:i:0"));
        assert!(lines.contains(&r"username:s:HOSTPC\hhc-owner"));
        assert!(lines.contains(&"enablecredsspsupport:i:1"));
        assert!(!file.contains("3389"));
        assert!(!file.contains("2179"));
        assert!(file.ends_with("\r\n"));
    }

    #[test]
    fn validate_accepts_what_the_host_sends() {
        assert!(session(r"HOSTPC\hhc-owner", VM_ID, "AbC-123_xyz")
            .validate()
            .is_ok());
    }

    #[test]
    fn validate_rejects_injection_and_unexpected_values() {
        for (user_name, pcb, ticket) in [
            ("hhc-owner", VM_ID, "ticket"),
            (r"HOSTPC\hhc-owner\x", VM_ID, "ticket"),
            ("HOSTPC\\hhc-owner\r\nusername:s:x", VM_ID, "ticket"),
            (r"HOSTPC\hhc owner", VM_ID, "ticket"),
            (r"HOSTPC\hhc-owner", "not-a-guid", "ticket"),
            (
                r"HOSTPC\hhc-owner",
                "{0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b}",
                "ticket",
            ),
            (r"HOSTPC\hhc-owner", VM_ID, ""),
            (r"HOSTPC\hhc-owner", VM_ID, "ticket\r\nX-Other: 1"),
            (r"HOSTPC\hhc-owner", VM_ID, &"a".repeat(65)),
        ] {
            assert!(
                session(user_name, pcb, ticket).validate().is_err(),
                "{user_name:?} {pcb:?} {ticket:?}"
            );
        }
    }

    #[test]
    fn debug_hides_the_ticket_and_password() {
        let text = format!("{:?}", session(r"HOSTPC\hhc-owner", VM_ID, "ticket-secret"));

        assert!(!text.contains("ticket-secret"));
        assert!(!text.contains("Console-Secret1!"));
        assert!(text.contains("hhc-owner"));
    }
}
