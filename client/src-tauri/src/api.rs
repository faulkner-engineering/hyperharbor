use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde::Deserialize;
use serde_json::json;
use sha2::{Digest, Sha256};

use crate::error::ClientError;
use crate::hosts::HostEntry;
use crate::identity::{pem_to_der, ClientIdentity};
use crate::paired::PairedHost;
use crate::spake2::ClientExchange;
use crate::tls;
use crate::wake::WakeAdapter;

const API_BASE_PATH: &str = "/api/v1";
const CONNECT_TIMEOUT: Duration = Duration::from_secs(2);
const REQUEST_TIMEOUT: Duration = Duration::from_secs(10);
/// PowerShell Direct calls in the guest take tens of seconds.
/// Linux setup can install xrdp and a desktop; the host allows 20 minutes for that.
const PROVISION_TIMEOUT: Duration = Duration::from_secs(25 * 60);
const VM_CONNECT_TIMEOUT: Duration = Duration::from_secs(90);

/// Options for POST /vms/{vmId}/provision. `install_desktop` and `trust_new_host_key` apply to Linux
/// guests only.
#[derive(Clone, Copy, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProvisionOptions {
    pub enable_remote_desktop: bool,
    pub install_desktop: bool,
    /// Accept an SSH host key that differs from the one pinned at the last setup.
    #[serde(default)]
    pub trust_new_host_key: bool,
}

#[derive(Deserialize)]
struct ProblemDetails {
    title: Option<String>,
    detail: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PairingRequestCreated {
    pairing_id: String,
    host_share: String,
    expires_at: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PairingResult {
    device_id: String,
    host_id: String,
    host_certificate_pem: String,
    host_confirmation: String,
}

/// A pairing request waiting for the user to type the PIN.
pub struct PendingPairing {
    pub pairing_id: String,
    pub expires_at: String,
    host_share: Vec<u8>,
    base_url: String,
    http: reqwest::Client,
    captured_certificate: Arc<Mutex<Option<Vec<u8>>>>,
}

/// HTTP client for host APIs. Paired hosts are reached over mTLS with a pinned certificate.
pub struct ApiClient {
    identity: Arc<ClientIdentity>,
    device_name: String,
    /// Pinned clients by host ID, so TLS sessions are reused.
    clients: Mutex<HashMap<String, reqwest::Client>>,
    /// The base URL that last worked for each host ID.
    last_good: Mutex<HashMap<String, String>>,
}

impl ApiClient {
    pub fn new(identity: Arc<ClientIdentity>, device_name: String) -> Self {
        Self {
            identity,
            device_name,
            clients: Mutex::new(HashMap::new()),
            last_good: Mutex::new(HashMap::new()),
        }
    }

    /// Returns the VM list exactly as the host serialized it; the frontend types it from docs/api.yaml.
    pub async fn list_vms(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
    ) -> Result<serde_json::Value, ClientError> {
        let response = self
            .send_paired(host, paired, reqwest::Method::GET, "/vms")
            .await?;
        response
            .json::<serde_json::Value>()
            .await
            .map_err(|e| ClientError::InvalidResponse(e.without_url().to_string()))
    }

    /// Asks the host to forget this device.
    pub async fn unpair(&self, host: &HostEntry, paired: &PairedHost) -> Result<(), ClientError> {
        self.send_paired(
            host,
            paired,
            reqwest::Method::DELETE,
            "/pairing/devices/self",
        )
        .await?;
        self.clients.lock().unwrap().remove(&paired.host_id);
        Ok(())
    }

    /// Step 1: asks the host to show a PIN and returns its SPAKE2 share.
    pub async fn start_pairing(&self, host: &HostEntry) -> Result<PendingPairing, ClientError> {
        let (config, captured_certificate) = tls::capturing(&self.identity);
        let http = build_client(config);
        let body = json!({
            "deviceName": self.device_name,
            "clientCertificatePem": self.identity.certificate_pem,
        });

        let mut last_error = None;
        for base_url in candidate_base_urls(host) {
            let result = http
                .post(format!("{base_url}{API_BASE_PATH}/pairing/requests"))
                .json(&body)
                .send()
                .await;
            match result {
                Ok(response) => {
                    let created: PairingRequestCreated = parse(check(response).await?).await?;
                    return Ok(PendingPairing {
                        pairing_id: created.pairing_id,
                        expires_at: created.expires_at,
                        host_share: decode_base64(&created.host_share)?,
                        base_url,
                        http,
                        captured_certificate,
                    });
                }
                // A request that may have reached the host (timeout) would create a second request.
                Err(error) if tries_next_address(&error) => last_error = Some(error),
                Err(error) => return Err(unreachable(Some(error))),
            }
        }

        Err(unreachable(last_error))
    }

    /// Ends a pending request on the host so its PIN window closes and other devices can pair.
    /// A request the host no longer knows about (404) is already gone, which is the goal.
    pub async fn cancel_pairing(&self, pending: &PendingPairing) -> Result<(), ClientError> {
        let response = pending
            .http
            .delete(format!(
                "{}{API_BASE_PATH}/pairing/requests/{}",
                pending.base_url, pending.pairing_id
            ))
            .send()
            .await
            .map_err(|e| unreachable(Some(e)))?;

        match check(response).await {
            Ok(_) | Err(ClientError::Api { status: 404, .. }) => Ok(()),
            Err(error) => Err(error),
        }
    }

    /// Step 2: proves knowledge of the PIN, verifies the host, and returns the pairing to store.
    pub async fn complete_pairing(
        &self,
        host: &HostEntry,
        pending: &PendingPairing,
        pin: &str,
    ) -> Result<PairedHost, ClientError> {
        let host_certificate = pending
            .captured_certificate
            .lock()
            .unwrap()
            .clone()
            .ok_or_else(|| {
                ClientError::InvalidResponse("no host certificate was presented".into())
            })?;
        let host_certificate_hash: [u8; 32] = Sha256::digest(&host_certificate).into();

        let exchange = ClientExchange::new(
            &pending.pairing_id,
            pin,
            &pending.host_share,
            &self.identity.certificate_hash(),
            &host_certificate_hash,
        )
        .ok_or_else(|| {
            ClientError::InvalidResponse("the host share is not a valid group element".into())
        })?;

        let response = pending
            .http
            .post(format!(
                "{}{API_BASE_PATH}/pairing/requests/{}/confirm",
                pending.base_url, pending.pairing_id
            ))
            .json(&json!({
                "clientShare": encode_base64(&exchange.client_share),
                "clientConfirmation": encode_base64(&exchange.client_confirmation),
            }))
            .send()
            .await
            .map_err(|e| unreachable(Some(e)))?;
        let result: PairingResult = parse(check(response).await?).await?;

        // The connection must still be talking to the certificate the exchange was bound to.
        let still_same = pending.captured_certificate.lock().unwrap().as_deref()
            == Some(host_certificate.as_slice());
        let returned_matches = pem_to_der(&result.host_certificate_pem).as_deref()
            == Some(host_certificate.as_slice());
        if !still_same
            || !returned_matches
            || !exchange.verify_host_confirmation(&decode_base64(&result.host_confirmation)?)
        {
            return Err(ClientError::PairingVerificationFailed);
        }

        Ok(PairedHost {
            host_id: result.host_id,
            device_id: result.device_id,
            host_certificate_fingerprint: hex::encode_upper(host_certificate_hash),
            display_name: host.display_name.clone(),
            entry_keys: vec![host.key.clone()],
            wake_adapters: Vec::new(),
            wake_refreshed_at: None,
            addresses: host.addresses.clone(),
            port: host.port,
            host_name: host.host_name.clone(),
        })
    }

    /// POST /vms/{vmId}/provision. The admin password goes to the host over mTLS and is not kept here.
    pub async fn provision_vm(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        vm_id: &str,
        admin_user_name: &str,
        admin_password: &str,
        options: ProvisionOptions,
    ) -> Result<serde_json::Value, ClientError> {
        let body = json!({
            "adminUserName": admin_user_name,
            "adminPassword": admin_password,
            "enableRemoteDesktop": options.enable_remote_desktop,
            "installDesktop": options.install_desktop,
            "trustNewHostKey": options.trust_new_host_key,
        });
        let response = self
            .send_paired_with(
                host,
                paired,
                reqwest::Method::POST,
                &vm_path(vm_id, "provision")?,
                Some(&body),
                Some(PROVISION_TIMEOUT),
            )
            .await?;
        parse(response).await
    }

    /// POST /vms/{vmId}/connect: Remote Desktop credentials for this User's account.
    pub async fn connect_vm(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        vm_id: &str,
    ) -> Result<crate::rdp::VmConnection, ClientError> {
        let response = self
            .send_paired_with(
                host,
                paired,
                reqwest::Method::POST,
                &vm_path(vm_id, "connect")?,
                None,
                Some(VM_CONNECT_TIMEOUT),
            )
            .await?;
        parse(response).await
    }

    /// GET /wake/info: the adapters to send magic packets to.
    pub async fn wake_info(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
    ) -> Result<Vec<WakeAdapter>, ClientError> {
        #[derive(Deserialize)]
        struct WakeInfo {
            adapters: Vec<WakeAdapter>,
        }

        let response = self
            .send_paired(host, paired, reqwest::Method::GET, "/wake/info")
            .await?;
        Ok(parse::<WakeInfo>(response).await?.adapters)
    }

    pub async fn wake_readiness(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
    ) -> Result<serde_json::Value, ClientError> {
        let response = self
            .send_paired(host, paired, reqwest::Method::GET, "/wake/readiness")
            .await?;
        parse(response).await
    }

    /// POST /wake/readiness/fix. Returns the new readiness when the host applied the fixes (200),
    /// or None when it is waiting for the user at the host to approve them (202).
    pub async fn fix_wake(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        check_ids: &[String],
    ) -> Result<Option<serde_json::Value>, ClientError> {
        let body = json!({ "checkIds": check_ids });
        let response = self
            .send_paired_with(
                host,
                paired,
                reqwest::Method::POST,
                "/wake/readiness/fix",
                Some(&body),
                None,
            )
            .await?;
        if response.status() == reqwest::StatusCode::ACCEPTED {
            return Ok(None);
        }
        Ok(Some(parse(response).await?))
    }

    /// POST /wake/test. The host sleeps after the delay; returns the WakeTestScheduled body.
    pub async fn start_wake_test(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        delay_seconds: u32,
    ) -> Result<serde_json::Value, ClientError> {
        let body = json!({ "delaySeconds": delay_seconds });
        let response = self
            .send_paired_with(
                host,
                paired,
                reqwest::Method::POST,
                "/wake/test",
                Some(&body),
                None,
            )
            .await?;
        parse(response).await
    }

    async fn send_paired(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        method: reqwest::Method,
        path: &str,
    ) -> Result<reqwest::Response, ClientError> {
        self.send_paired_with(host, paired, method, path, None, None)
            .await
    }

    async fn send_paired_with(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        method: reqwest::Method,
        path: &str,
        body: Option<&serde_json::Value>,
        timeout: Option<Duration>,
    ) -> Result<reqwest::Response, ClientError> {
        let http = self.pinned_client(paired)?;

        let mut candidates = candidate_base_urls(host);
        if let Some(good) = self.last_good.lock().unwrap().get(&paired.host_id) {
            candidates.retain(|url| url != good);
            candidates.insert(0, good.clone());
        }

        let mut last_error = None;
        for base_url in candidates {
            let mut request =
                http.request(method.clone(), format!("{base_url}{API_BASE_PATH}{path}"));
            if let Some(body) = body {
                request = request.json(body);
            }
            if let Some(timeout) = timeout {
                request = request.timeout(timeout);
            }
            match request.send().await {
                Ok(response) => {
                    self.last_good
                        .lock()
                        .unwrap()
                        .insert(paired.host_id.clone(), base_url);
                    return check(response).await;
                }
                // Only a failed connection proves the request never reached the host. After a
                // timeout the host may still be working (provisioning, rotating), so retrying at
                // another address could repeat a non-idempotent request.
                Err(error) if tries_next_address(&error) => last_error = Some(error),
                Err(error) => return Err(unreachable(Some(error))),
            }
        }

        Err(unreachable(last_error))
    }

    fn pinned_client(&self, paired: &PairedHost) -> Result<reqwest::Client, ClientError> {
        let mut clients = self.clients.lock().unwrap();
        if let Some(client) = clients.get(&paired.host_id) {
            return Ok(client.clone());
        }
        let hash = paired
            .certificate_hash()
            .ok_or_else(|| ClientError::Storage("the stored host fingerprint is invalid".into()))?;
        let client = build_client(tls::pinned(&self.identity, hash));
        clients.insert(paired.host_id.clone(), client.clone());
        Ok(client)
    }
}

/// Only a failed connection proves a request never reached the host, so only then is the next
/// address tried.
fn tries_next_address(error: &reqwest::Error) -> bool {
    error.is_connect()
}

/// "/vms/{vmId}/{action}". The ID comes from the frontend; only a GUID may become part of a path.
fn vm_path(vm_id: &str, action: &str) -> Result<String, ClientError> {
    if crate::hosts::is_guid(vm_id) {
        Ok(format!("/vms/{vm_id}/{action}"))
    } else {
        Err(ClientError::InvalidVmId)
    }
}

fn build_client(config: rustls::ClientConfig) -> reqwest::Client {
    reqwest::Client::builder()
        .tls_backend_preconfigured(config)
        .connect_timeout(CONNECT_TIMEOUT)
        .timeout(REQUEST_TIMEOUT)
        .build()
        .expect("failed to build HTTP client")
}

/// URLs to try for a host: loopback first for this machine, then advertised addresses.
pub fn candidate_base_urls(host: &HostEntry) -> Vec<String> {
    let mut urls = Vec::new();
    if host.is_local {
        urls.push(format!("https://127.0.0.1:{}", host.port));
    }
    for address in &host.addresses {
        let authority = match address.parse::<std::net::IpAddr>() {
            Ok(std::net::IpAddr::V6(v6)) => format!("[{v6}]"),
            Ok(ip) => ip.to_string(),
            Err(_) => address.clone(),
        };
        let url = format!("https://{authority}:{}", host.port);
        if !urls.contains(&url) {
            urls.push(url);
        }
    }
    urls
}

async fn check(response: reqwest::Response) -> Result<reqwest::Response, ClientError> {
    let status = response.status();
    if status.is_success() {
        return Ok(response);
    }

    let problem = response.json::<ProblemDetails>().await.ok();
    let message = problem
        .as_ref()
        .and_then(|p| p.detail.clone().filter(|d| !d.is_empty()))
        .or_else(|| problem.as_ref().and_then(|p| p.title.clone()))
        .unwrap_or_else(|| {
            status
                .canonical_reason()
                .unwrap_or("Request failed")
                .to_string()
        });

    Err(ClientError::Api {
        status: status.as_u16(),
        message,
    })
}

async fn parse<T: serde::de::DeserializeOwned>(
    response: reqwest::Response,
) -> Result<T, ClientError> {
    response
        .json::<T>()
        .await
        .map_err(|e| ClientError::InvalidResponse(e.without_url().to_string()))
}

fn unreachable(error: Option<reqwest::Error>) -> ClientError {
    ClientError::Unreachable(match error {
        Some(error) => {
            let error = error.without_url();
            let mut message = error.to_string();
            let mut source = std::error::Error::source(&error);
            while let Some(inner) = source {
                message = format!("{message}: {inner}");
                source = inner.source();
            }
            message
        }
        None => "the host has no known address".into(),
    })
}

fn decode_base64(value: &str) -> Result<Vec<u8>, ClientError> {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD
        .decode(value)
        .map_err(|e| ClientError::InvalidResponse(e.to_string()))
}

fn encode_base64(bytes: &[u8]) -> String {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.encode(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hosts::ManualHost;

    #[test]
    fn candidates_put_loopback_first_for_local_hosts() {
        let mut host = ManualHost::parse("localhost:49000").unwrap().to_entry("pc");
        host.addresses = vec!["192.168.1.5".into(), "fe80::1".into()];

        assert_eq!(
            candidate_base_urls(&host),
            vec![
                "https://127.0.0.1:49000",
                "https://192.168.1.5:49000",
                "https://[fe80::1]:49000"
            ]
        );
    }

    /// Live pairing against a running host over a non-loopback address. A stand-in tray must write
    /// the PIN to HH_PIN_FILE. Run with:
    /// HH_LIVE_HOST=192.168.0.70 HH_PIN_FILE=pin.txt cargo test live_pairing -- --ignored --nocapture
    #[tokio::test]
    #[ignore]
    async fn live_pairing() {
        let address = std::env::var("HH_LIVE_HOST").expect("HH_LIVE_HOST");
        let pin_file = std::env::var("HH_PIN_FILE").expect("HH_PIN_FILE");
        let _ = std::fs::remove_file(&pin_file);

        let (identity, _) = ClientIdentity::generate("live-test").unwrap();
        let api = ApiClient::new(Arc::new(identity), "Live Test Client".into());
        let host = ManualHost::parse(&address)
            .unwrap()
            .to_entry("not-this-machine");
        assert!(
            !host.is_local,
            "the live test must use a non-loopback address"
        );

        let pending = api.start_pairing(&host).await.expect("start pairing");
        println!("pairing {} started", pending.pairing_id);

        let pin = wait_for_pin(&pin_file).await;
        let wrong_pin = if pin == "000000" { "000001" } else { "000000" };
        let wrong = api.complete_pairing(&host, &pending, wrong_pin).await;
        assert!(
            matches!(wrong, Err(ClientError::Api { status: 401, .. })),
            "wrong PIN: {wrong:?}"
        );

        let paired = api
            .complete_pairing(&host, &pending, &pin)
            .await
            .expect("complete pairing");
        println!(
            "paired as device {} with host {}",
            paired.device_id, paired.host_id
        );

        let vms = api
            .list_vms(&host, &paired)
            .await
            .expect("list VMs over mTLS");
        println!("VMs: {vms}");
        assert!(vms.is_array());

        // Wake endpoints. The test never calls /wake/test, which would put the host to sleep.
        let adapters = api.wake_info(&host, &paired).await.expect("wake info");
        println!("wake adapters: {adapters:?}");
        let readiness = api
            .wake_readiness(&host, &paired)
            .await
            .expect("wake readiness");
        println!("wake readiness: {readiness}");
        assert!(readiness["checks"].is_array());
        let fix = api
            .fix_wake(&host, &paired, &["nicAllowWake".to_string()])
            .await
            .expect("fix request");
        assert!(
            fix.is_none(),
            "an unelevated host must ask for approval instead of applying fixes"
        );

        let mut wrong_pin_host = paired.clone();
        wrong_pin_host.host_id = "different".into();
        wrong_pin_host.host_certificate_fingerprint = "00".repeat(32);
        let rejected = api.list_vms(&host, &wrong_pin_host).await;
        assert!(
            matches!(rejected, Err(ClientError::Unreachable(_))),
            "pin mismatch: {rejected:?}"
        );

        api.unpair(&host, &paired).await.expect("unpair");
        let after = api.list_vms(&host, &paired).await;
        assert!(
            matches!(after, Err(ClientError::Api { status: 401, .. })),
            "after unpair: {after:?}"
        );
    }

    async fn wait_for_pin(path: &str) -> String {
        for _ in 0..120 {
            if let Ok(pin) = std::fs::read_to_string(path) {
                if pin.trim().len() == 6 {
                    return pin.trim().to_string();
                }
            }
            tokio::time::sleep(Duration::from_millis(500)).await;
        }
        panic!("no PIN was written to {path}");
    }

    #[test]
    fn candidates_use_advertised_addresses_for_remote_hosts() {
        let host = ManualHost::parse("pc.lan").unwrap().to_entry("other");
        assert_eq!(candidate_base_urls(&host), vec!["https://pc.lan:48443"]);
    }
}

/// Requests against an in-process HTTPS server: certificate pinning, which errors move on to the
/// next address, error mapping, and pairing verification.
#[cfg(test)]
mod server_tests {
    use super::*;
    use crate::hosts::HostSource;
    use crate::identity::ClientIdentity;
    use crate::test_server::{closed_address, Reply, Request, TestServer};

    const VM_ID: &str = "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b";

    fn api() -> ApiClient {
        let (identity, _) = ClientIdentity::generate("test").unwrap();
        ApiClient::new(Arc::new(identity), "Test Device".into())
    }

    fn host(addresses: &[&str], port: u16) -> HostEntry {
        HostEntry {
            key: "test".into(),
            display_name: "Test Host".into(),
            host_id: Some("host-1".into()),
            host_name: None,
            addresses: addresses.iter().map(|a| a.to_string()).collect(),
            port,
            api_version: None,
            source: HostSource::Manual,
            is_local: false,
            paired: true,
            can_wake: false,
        }
    }

    fn paired(certificate_hash: [u8; 32]) -> PairedHost {
        PairedHost {
            host_id: "host-1".into(),
            device_id: "device-1".into(),
            host_certificate_fingerprint: hex::encode_upper(certificate_hash),
            display_name: "Test Host".into(),
            entry_keys: vec!["test".into()],
            wake_adapters: Vec::new(),
            wake_refreshed_at: None,
            addresses: Vec::new(),
            port: 0,
            host_name: None,
        }
    }

    fn ok_list(_: &Request) -> Reply {
        Reply::json(200, "[]")
    }

    #[tokio::test]
    async fn pinned_certificate_is_accepted() {
        let server = TestServer::start("127.0.0.1:0", ok_list);
        let target = host(&["127.0.0.1"], server.address.port());

        let vms = api()
            .list_vms(&target, &paired(server.certificate_hash()))
            .await;

        assert_eq!(vms.unwrap(), json!([]));
        assert_eq!(server.requests().len(), 1);
    }

    #[tokio::test]
    async fn different_certificate_is_rejected_before_any_request() {
        let server = TestServer::start("127.0.0.1:0", ok_list);
        let target = host(&["127.0.0.1"], server.address.port());

        let result = api().list_vms(&target, &paired([0xAB; 32])).await;

        assert!(
            matches!(result, Err(ClientError::Unreachable(_))),
            "{result:?}"
        );
        assert!(server.requests().is_empty(), "a request reached the host");
    }

    #[test]
    fn capturing_config_records_the_presented_certificate() {
        let server = TestServer::start("127.0.0.1:0", ok_list);
        let (identity, _) = ClientIdentity::generate("test").unwrap();
        let (config, slot) = tls::capturing(&identity);
        let mut connection = rustls::ClientConnection::new(
            Arc::new(config),
            rustls::pki_types::ServerName::try_from("localhost").unwrap(),
        )
        .unwrap();
        let mut socket = std::net::TcpStream::connect(server.address).unwrap();

        while connection.is_handshaking() {
            connection.complete_io(&mut socket).unwrap();
        }

        assert_eq!(
            slot.lock().unwrap().as_deref(),
            Some(server.certificate_der.as_slice())
        );
    }

    #[tokio::test]
    async fn refused_connection_moves_on_to_the_next_address() {
        // Two loopback addresses share one port: nothing listens on 127.0.0.2.
        let server = TestServer::start("127.0.0.1:0", ok_list);
        let target = host(&["127.0.0.2", "127.0.0.1"], server.address.port());
        let api = api();

        api.list_vms(&target, &paired(server.certificate_hash()))
            .await
            .unwrap();

        let last_good = api.last_good.lock().unwrap().get("host-1").cloned();
        assert_eq!(
            last_good,
            Some(format!("https://127.0.0.1:{}", server.address.port()))
        );
    }

    #[tokio::test]
    async fn timeout_does_not_retry_at_another_address() {
        let slow = TestServer::start("127.0.0.1:0", |_| {
            Reply::json(200, "{}").after(Duration::from_secs(5))
        });
        let port = slow.address.port();
        // Same certificate, so a repeated request would pass pinning and be recorded.
        let other = slow
            .start_sharing_certificate(&format!("127.0.0.2:{port}"), |_| Reply::json(200, "{}"));
        let target = host(&["127.0.0.1", "127.0.0.2"], port);

        let result = api()
            .send_paired_with(
                &target,
                &paired(slow.certificate_hash()),
                reqwest::Method::POST,
                &vm_path(VM_ID, "connect").unwrap(),
                None,
                // Long enough that the deadline falls after the request is sent even when tests
                // run in parallel. A deadline during the handshake is a connect error, which is
                // correctly retried because nothing reached the host.
                Some(Duration::from_secs(2)),
            )
            .await;

        assert!(
            matches!(result, Err(ClientError::Unreachable(_))),
            "{result:?}"
        );
        assert_eq!(slow.requests().len(), 1);
        assert!(other.requests().is_empty(), "the request was repeated");
    }

    #[tokio::test]
    async fn problem_details_become_api_errors() {
        let server = TestServer::start("127.0.0.1:0", |request| {
            if request.path.ends_with("/connect") {
                Reply::json(
                    409,
                    r#"{"title":"Cannot provision","status":409,"detail":"Start the VM first."}"#,
                )
            } else {
                Reply::json(503, r#"{"title":"Hyper-V unavailable","status":503}"#)
            }
        });
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();
        let paired = paired(server.certificate_hash());

        let connect = api.connect_vm(&target, &paired, VM_ID).await;
        let list = api.list_vms(&target, &paired).await;

        assert!(
            matches!(&connect, Err(ClientError::Api { status: 409, message }) if message == "Start the VM first."),
            "{connect:?}"
        );
        assert!(
            matches!(&list, Err(ClientError::Api { status: 503, message }) if message == "Hyper-V unavailable"),
            "{list:?}"
        );
    }

    #[tokio::test]
    async fn vm_ids_that_are_not_guids_never_reach_the_host() {
        let server = TestServer::start("127.0.0.1:0", ok_list);
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();
        let paired = paired(server.certificate_hash());

        for vm_id in [
            "../pairing/devices/self",
            "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b/../x",
            "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8?",
            "0b9a6f53x1c2d-4e8f-a1b2-3c4d5e6f7a8b",
            "",
        ] {
            let result = api.connect_vm(&target, &paired, vm_id).await;
            assert!(
                matches!(result, Err(ClientError::InvalidVmId)),
                "{vm_id}: {result:?}"
            );
        }
        assert!(server.requests().is_empty());
    }

    fn vector(name: &str) -> Vec<u8> {
        let vectors: HashMap<String, String> = serde_json::from_str(include_str!(
            "../../../host/tests/Host.Tests/Pairing/Spake2Vectors.json"
        ))
        .unwrap();
        hex::decode(&vectors[name]).unwrap()
    }

    /// A pairing host whose confirm response returns `certificate_pem`, or its own certificate when
    /// None. It never knows the PIN, so its confirmation is always wrong.
    fn pairing_server(certificate_pem: Option<String>) -> TestServer {
        let host_share = encode_base64(&vector("Y"));
        let returned = Arc::new(Mutex::new(certificate_pem.clone().unwrap_or_default()));
        let returned_in_handler = returned.clone();
        let server = TestServer::start("127.0.0.1:0", move |request| {
            match (request.method.as_str(), request.path.as_str()) {
                ("POST", "/api/v1/pairing/requests") => Reply::json(
                    201,
                    json!({
                        "pairingId": "3f2a9c1d-4b5e-4f70-8192-a3b4c5d6e7f8",
                        "hostShare": host_share,
                        "expiresAt": "2030-01-01T00:00:00Z",
                    })
                    .to_string(),
                ),
                ("POST", _) => Reply::json(
                    200,
                    json!({
                        "deviceId": "device-1",
                        "hostId": "host-1",
                        "userId": "user-1",
                        "hostCertificatePem": *returned_in_handler.lock().unwrap(),
                        "hostConfirmation": encode_base64(&[0u8; 32]),
                    })
                    .to_string(),
                ),
                _ => Reply::json(404, r#"{"title":"Pairing request not found","status":404}"#),
            }
        });
        if certificate_pem.is_none() {
            *returned.lock().unwrap() = server.certificate_pem.clone();
        }
        server
    }

    #[tokio::test]
    async fn pairing_rejects_a_host_that_returns_another_certificate() {
        let other = TestServer::start("127.0.0.1:0", ok_list);
        let server = pairing_server(Some(other.certificate_pem.clone()));
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();

        let pending = api.start_pairing(&target).await.unwrap();
        let result = api.complete_pairing(&target, &pending, "482913").await;

        assert!(
            matches!(result, Err(ClientError::PairingVerificationFailed)),
            "{result:?}"
        );
        let confirm = &server.requests()[1];
        assert!(
            confirm.body.contains("clientShare") && confirm.body.contains("clientConfirmation")
        );
        assert!(!confirm.body.contains("482913"), "the PIN was sent");
    }

    #[tokio::test]
    async fn pairing_rejects_a_host_that_cannot_prove_the_pin() {
        let server = pairing_server(None);
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();

        let pending = api.start_pairing(&target).await.unwrap();
        let result = api.complete_pairing(&target, &pending, "482913").await;

        assert!(
            matches!(result, Err(ClientError::PairingVerificationFailed)),
            "{result:?}"
        );
    }

    #[tokio::test]
    async fn cancel_treats_an_unknown_request_as_cancelled() {
        let server = pairing_server(None);
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();
        let pending = api.start_pairing(&target).await.unwrap();

        api.cancel_pairing(&pending).await.unwrap();

        assert_eq!(server.requests().last().unwrap().method, "DELETE");
    }

    #[tokio::test]
    async fn pairing_with_nothing_listening_is_unreachable() {
        let target = host(&["127.0.0.1"], closed_address().port());

        let result = api().start_pairing(&target).await;

        assert!(
            matches!(result, Err(ClientError::Unreachable(_))),
            "{:?}",
            result.err()
        );
    }

    /// The real host service on loopback, with this test acting as its tray app.
    struct LoopbackHost {
        process: std::process::Child,
        port: u16,
        pipe_name: String,
        data_directory: std::path::PathBuf,
    }

    impl LoopbackHost {
        fn start(service_exe: &str) -> Self {
            let port = closed_address().port();
            let id = format!("{}-{port}", std::process::id());
            let pipe_name = format!("HyperHarbor.E2E.{id}");
            let data_directory = std::env::temp_dir().join(format!("hyperharbor-e2e-{id}"));
            let process = std::process::Command::new(service_exe)
                .args(["--DataDirectory", data_directory.to_str().unwrap()])
                .args(["--Api:Port", &port.to_string()])
                .args(["--Api:ListenAddress", "127.0.0.1"])
                .args(["--Discovery:Enabled", "false"])
                .args(["--Tray:PipeName", &pipe_name])
                .stdout(std::process::Stdio::null())
                .spawn()
                .expect("start the host service");

            let deadline = std::time::Instant::now() + Duration::from_secs(60);
            while std::net::TcpStream::connect(("127.0.0.1", port)).is_err() {
                assert!(
                    std::time::Instant::now() < deadline,
                    "the service did not listen"
                );
                std::thread::sleep(Duration::from_millis(200));
            }
            Self {
                process,
                port,
                pipe_name,
                data_directory,
            }
        }

        /// Connects to the tray pipe and returns a receiver for the PINs the host shows.
        fn tray(&self) -> std::sync::mpsc::Receiver<String> {
            use std::io::BufRead;
            let pipe = std::fs::OpenOptions::new()
                .read(true)
                .write(true)
                .open(format!(r"\\.\pipe\{}", self.pipe_name))
                .expect("connect to the tray pipe");
            let mut lines = std::io::BufReader::new(pipe).lines();
            // The first message (the device list) means the host has registered this tray.
            lines.next().expect("device list").unwrap();

            let (sender, receiver) = std::sync::mpsc::channel();
            std::thread::spawn(move || {
                for line in lines.map_while(Result::ok) {
                    let message: serde_json::Value = serde_json::from_str(&line).unwrap();
                    if message["type"] == "pairingStarted" {
                        let _ = sender.send(message["pin"].as_str().unwrap().to_string());
                    }
                }
            });
            receiver
        }
    }

    impl Drop for LoopbackHost {
        fn drop(&mut self) {
            let _ = self.process.kill();
            let _ = self.process.wait();
            let _ = std::fs::remove_dir_all(&self.data_directory);
        }
    }

    /// End to end against the real host service, on loopback only. Run with scripts\e2e.ps1, or:
    /// HH_E2E_SERVICE_EXE=<path to HyperHarbor.Host.Service.exe> cargo test e2e_loopback -- --ignored
    #[tokio::test]
    #[ignore]
    async fn e2e_loopback() {
        let service_exe = std::env::var("HH_E2E_SERVICE_EXE").expect("HH_E2E_SERVICE_EXE");
        let service = LoopbackHost::start(&service_exe);
        let pins = service.tray();
        let target = host(&["127.0.0.1"], service.port);
        let api = api();

        let pending = api.start_pairing(&target).await.expect("start pairing");
        let pin = pins
            .recv_timeout(Duration::from_secs(10))
            .expect("the host showed a PIN");
        let wrong_pin = if pin == "000000" { "000001" } else { "000000" };
        let wrong = api.complete_pairing(&target, &pending, wrong_pin).await;
        assert!(
            matches!(wrong, Err(ClientError::Api { status: 401, .. })),
            "wrong PIN: {wrong:?}"
        );
        let paired = api
            .complete_pairing(&target, &pending, &pin)
            .await
            .expect("complete pairing");

        // Hyper-V may be missing (CI); then the host answers 503, which still proves mTLS worked.
        match api.list_vms(&target, &paired).await {
            Ok(vms) => assert!(vms.is_array()),
            Err(ClientError::Api { status: 503, .. }) => {}
            Err(error) => panic!("list VMs: {error:?}"),
        }
        let readiness = api
            .wake_readiness(&target, &paired)
            .await
            .expect("wake readiness");
        assert!(readiness["checks"].is_array());

        let mut other_pin = paired.clone();
        other_pin.host_id = "different".into();
        other_pin.host_certificate_fingerprint = "00".repeat(32);
        assert!(matches!(
            api.list_vms(&target, &other_pin).await,
            Err(ClientError::Unreachable(_))
        ));

        api.unpair(&target, &paired).await.expect("unpair");
        assert!(matches!(
            api.list_vms(&target, &paired).await,
            Err(ClientError::Api { status: 401, .. })
        ));
    }

    /// Wire samples written by the host's ContractFixtureTests (checked there against api.yaml).
    fn fixture(schema: &str) -> serde_json::Value {
        let all: serde_json::Value = serde_json::from_str(include_str!(
            "../../../host/tests/Host.Tests/Contracts/ContractFixtures.json"
        ))
        .unwrap();
        all[schema].clone()
    }

    fn keys(value: &serde_json::Value) -> Vec<String> {
        let mut keys: Vec<String> = value.as_object().unwrap().keys().cloned().collect();
        keys.sort();
        keys
    }

    #[test]
    fn host_responses_parse_into_client_types() {
        let created: PairingRequestCreated =
            serde_json::from_value(fixture("PairingRequestCreated")).unwrap();
        assert_eq!(created.pairing_id, "3f2a9c1d-4b5e-4f70-8192-a3b4c5d6e7f8");
        assert_eq!(decode_base64(&created.host_share).unwrap(), [1, 2, 3]);

        let result: PairingResult = serde_json::from_value(fixture("PairingResult")).unwrap();
        assert_eq!(
            decode_base64(&result.host_confirmation).unwrap(),
            [10, 11, 12]
        );

        let connection: crate::rdp::VmConnection =
            serde_json::from_value(fixture("VmConnection")).unwrap();
        assert_eq!(connection.guest_os, crate::rdp::GuestOs::Windows);
        assert_eq!(connection.port, 3389);
        connection.validate().unwrap();

        let adapters: Vec<WakeAdapter> =
            serde_json::from_value(fixture("WakeInfo")["adapters"].clone()).unwrap();
        assert_eq!(adapters[0].mac_address, "00155D012345");
    }

    #[tokio::test]
    async fn request_bodies_use_the_contract_property_names() {
        let server = pairing_server(None);
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();
        let paired = paired(server.certificate_hash());

        let pending = api.start_pairing(&target).await.unwrap();
        let _ = api.complete_pairing(&target, &pending, "482913").await;
        let options = ProvisionOptions {
            enable_remote_desktop: true,
            install_desktop: true,
            trust_new_host_key: true,
        };
        let _ = api
            .provision_vm(&target, &paired, VM_ID, "hhadmin", "x", options)
            .await;
        let _ = api
            .fix_wake(&target, &paired, &["magicPacket".to_string()])
            .await;
        let _ = api.start_wake_test(&target, &paired, 30).await;

        let bodies: Vec<serde_json::Value> = server
            .requests()
            .iter()
            .map(|request| serde_json::from_str(&request.body).unwrap())
            .collect();
        for (body, schema) in bodies.iter().zip([
            "PairingRequest",
            "PairingConfirmation",
            "ProvisionVmRequest",
            "WakeFixRequest",
            "WakeTestRequest",
        ]) {
            assert_eq!(keys(body), keys(&fixture(schema)), "{schema}");
        }
        assert_eq!(bodies.len(), 5);
    }
}
