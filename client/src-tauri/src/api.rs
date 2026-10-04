use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde::Deserialize;
use serde_json::json;
use sha2::{Digest, Sha256};

use crate::error::{ClientError, Issue};
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
/// Opening a console grants access through Hyper-V and rotates a host account password; both are quick.
const CONSOLE_OPEN_TIMEOUT: Duration = Duration::from_secs(30);
/// Until the host answers a tunnel request; the tunnel itself has no time limit.
const CONSOLE_TUNNEL_TIMEOUT: Duration = Duration::from_secs(15);
/// ISO images are several gigabytes; a slow network can take hours.
const UPLOAD_TIMEOUT: Duration = Duration::from_secs(12 * 60 * 60);
/// Bytes read from disk per chunk of an upload.
const UPLOAD_CHUNK_BYTES: usize = 1024 * 1024;

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

/// Body of POST /vms/{vmId}/delete (api.yaml VmDeleteRequest).
#[derive(Clone, Debug, Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeleteVmRequest {
    pub delete_disks: bool,
    pub delete_checkpoints: bool,
    pub confirm_name: String,
}

/// Body of POST /vms (api.yaml CreateVmRequest).
#[derive(Clone, Debug, Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateVmRequest {
    pub name: String,
    pub iso_name: String,
    pub disk_size_gb: u32,
    pub processor_count: u32,
    pub startup_memory_mb: u64,
    pub maximum_memory_mb: u64,
    pub dynamic_memory: bool,
    pub switch_id: Option<String>,
    pub enable_tpm: bool,
    pub acknowledge_warnings: bool,
    /// Install the OS unattended with a profile; none means a manual install from the console.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub install: Option<UnattendedInstallRequest>,
}

/// api.yaml UnattendedInstallRequest. Unset fields use the profile's defaults.
#[derive(Clone, Debug, Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UnattendedInstallRequest {
    pub profile_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub windows_edition: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub computer_name: Option<String>,
}

/// Body of PATCH /vms/{vmId}/compute (api.yaml UpdateVmComputeRequest). Unset fields stay as they are.
#[derive(Clone, Debug, Default, Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateComputeRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub processor_count: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub startup_memory_mb: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub maximum_memory_mb: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dynamic_memory: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub nested_virtualization: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mac_address_spoofing: Option<bool>,
    #[serde(default)]
    pub shut_down_to_apply: bool,
    #[serde(default)]
    pub acknowledge_warnings: bool,
}

/// The power actions in api.yaml VmAction.
pub const VM_ACTIONS: [&str; 5] = ["start", "shutdown", "turnOff", "save", "restart"];

#[derive(Deserialize)]
struct ProblemDetails {
    title: Option<String>,
    detail: Option<String>,
    code: Option<String>,
    #[serde(default)]
    errors: Vec<Issue>,
    #[serde(default)]
    warnings: Vec<Issue>,
}

/// The host's problem code when a request lacks a valid elevation token.
pub const ELEVATION_REQUIRED: &str = "elevationRequired";

/// Request header for the elevation token (the api.yaml elevation security scheme).
const ELEVATION_HEADER: &str = "X-HyperHarbor-Elevation";

/// Request header that carries a console ticket (api.yaml openVmConsoleTunnel).
const CONSOLE_TICKET_HEADER: &str = "X-HyperHarbor-Console-Ticket";
/// The Upgrade protocol of a console tunnel.
const CONSOLE_UPGRADE_PROTOCOL: &str = "hyperharbor-console";

/// An elevation token for one host. Kept only in memory, and never passed to the frontend.
struct Elevation {
    token: zeroize::Zeroizing<String>,
}

/// What the frontend may know about an elevation: when it ends.
#[derive(Clone, Debug, Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ElevationGranted {
    pub expires_at: String,
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
    /// Elevation tokens by host ID. In memory only; a restart of the client drops them.
    elevations: Mutex<HashMap<String, Elevation>>,
}

impl ApiClient {
    pub fn new(identity: Arc<ClientIdentity>, device_name: String) -> Self {
        Self {
            identity,
            device_name,
            clients: Mutex::new(HashMap::new()),
            last_good: Mutex::new(HashMap::new()),
            elevations: Mutex::new(HashMap::new()),
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

    /// POST /vms/{vmId}/console: the host console account, a fresh password, and a tunnel ticket.
    pub async fn open_console(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        vm_id: &str,
    ) -> Result<crate::console::ConsoleSession, ClientError> {
        let response = self
            .send_paired_with(
                host,
                paired,
                reqwest::Method::POST,
                &vm_path(vm_id, "console")?,
                None,
                Some(CONSOLE_OPEN_TIMEOUT),
            )
            .await?;
        parse(response).await
    }

    /// POST /vms/{vmId}/console/tunnel as an HTTP/1.1 upgrade. After the 101 response the returned
    /// stream carries raw Remote Desktop bytes to the host's console service. The timeout covers only
    /// the response; an upgraded stream has no body, so the client-wide timeout does not end it.
    pub async fn open_console_tunnel(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        vm_id: &str,
        ticket: &str,
    ) -> Result<reqwest::Upgraded, ClientError> {
        let path = vm_path(vm_id, "console/tunnel")?;
        let http = self.pinned_client(paired)?;
        let mut last_error = None;
        for base_url in self.ordered_candidates(host, paired) {
            let request = http
                .post(format!("{base_url}{API_BASE_PATH}{path}"))
                .header(reqwest::header::CONNECTION, "Upgrade")
                .header(reqwest::header::UPGRADE, CONSOLE_UPGRADE_PROTOCOL)
                .header(CONSOLE_TICKET_HEADER, ticket)
                .timeout(CONSOLE_TUNNEL_TIMEOUT);
            match request.send().await {
                Ok(response) if response.status() == reqwest::StatusCode::SWITCHING_PROTOCOLS => {
                    return response.upgrade().await.map_err(|e| {
                        ClientError::InvalidResponse(format!(
                            "the console tunnel did not open: {e}"
                        ))
                    });
                }
                Ok(response) => {
                    check(response).await?;
                    return Err(ClientError::InvalidResponse(
                        "the host did not open the console tunnel".into(),
                    ));
                }
                Err(error) if tries_next_address(&error) => last_error = Some(error),
                Err(error) => return Err(unreachable(Some(error))),
            }
        }

        Err(unreachable(last_error))
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

    /// POST /auth/elevation. Keeps the token for this host in memory and returns only its expiry,
    /// so the token never reaches the webview.
    pub async fn elevate(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        passphrase: &str,
    ) -> Result<ElevationGranted, ClientError> {
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Grant {
            token: String,
            expires_at: String,
        }

        let body = json!({ "passphrase": passphrase });
        let response = self
            .send_paired_with(
                host,
                paired,
                reqwest::Method::POST,
                "/auth/elevation",
                Some(&body),
                None,
            )
            .await?;
        let grant: Grant = parse(response).await?;
        self.elevations.lock().unwrap().insert(
            paired.host_id.clone(),
            Elevation {
                token: zeroize::Zeroizing::new(grant.token),
            },
        );
        Ok(ElevationGranted {
            expires_at: grant.expires_at,
        })
    }

    /// GET /auth/elevation.
    pub async fn elevation_status(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
    ) -> Result<serde_json::Value, ClientError> {
        self.get_json(host, paired, "/auth/elevation").await
    }

    /// DELETE /auth/elevation, and forgets the token here even when the host cannot be reached.
    pub async fn drop_elevation(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
    ) -> Result<(), ClientError> {
        let result = self
            .send_paired(host, paired, reqwest::Method::DELETE, "/auth/elevation")
            .await;
        self.elevations.lock().unwrap().remove(&paired.host_id);
        result.map(|_| ())
    }

    /// POST /vms/{vmId}/actions. turnOff needs elevation.
    pub async fn perform_vm_action(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        vm_id: &str,
        action: &str,
    ) -> Result<serde_json::Value, ClientError> {
        if !VM_ACTIONS.contains(&action) {
            return Err(ClientError::InvalidRequest(format!(
                "unknown power action {action}"
            )));
        }
        let body = json!({ "action": action });
        self.send_json(
            host,
            paired,
            reqwest::Method::POST,
            &vm_path(vm_id, "actions")?,
            &body,
        )
        .await
    }

    /// GET /vms/{vmId}/delete-preview.
    pub async fn delete_preview(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        vm_id: &str,
    ) -> Result<serde_json::Value, ClientError> {
        self.get_json(host, paired, &vm_path(vm_id, "delete-preview")?)
            .await
    }

    /// POST /vms/{vmId}/delete. Returns the deletion job.
    pub async fn delete_vm(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        vm_id: &str,
        request: &DeleteVmRequest,
    ) -> Result<serde_json::Value, ClientError> {
        self.send_json(
            host,
            paired,
            reqwest::Method::POST,
            &vm_path(vm_id, "delete")?,
            &to_body(request)?,
        )
        .await
    }

    /// GET /jobs/{jobId}.
    pub async fn get_job(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        job_id: &str,
    ) -> Result<serde_json::Value, ClientError> {
        if !crate::hosts::is_guid(job_id) {
            return Err(ClientError::InvalidRequest(
                "the job ID is not valid".into(),
            ));
        }
        self.get_json(host, paired, &format!("/jobs/{job_id}"))
            .await
    }

    /// POST /vms. Returns the creation job.
    pub async fn create_vm(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        request: &CreateVmRequest,
    ) -> Result<serde_json::Value, ClientError> {
        self.send_json(
            host,
            paired,
            reqwest::Method::POST,
            "/vms",
            &to_body(request)?,
        )
        .await
    }

    /// GET on a fixed, read-only path: /host/resources, /isos, or /switches.
    pub async fn get_resource(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        resource: HostResource,
    ) -> Result<serde_json::Value, ClientError> {
        self.get_json(host, paired, resource.path()).await
    }

    /// GET /vms/{vmId}/compute.
    pub async fn get_vm_compute(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        vm_id: &str,
    ) -> Result<serde_json::Value, ClientError> {
        self.get_json(host, paired, &vm_path(vm_id, "compute")?)
            .await
    }

    /// PATCH /vms/{vmId}/compute. Returns `{ settings, job }`.
    pub async fn update_vm_compute(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        vm_id: &str,
        request: &UpdateComputeRequest,
    ) -> Result<serde_json::Value, ClientError> {
        self.send_json(
            host,
            paired,
            reqwest::Method::PATCH,
            &vm_path(vm_id, "compute")?,
            &to_body(request)?,
        )
        .await
    }

    /// PATCH /isos/{name}.
    pub async fn rename_iso(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        name: &str,
        new_name: &str,
    ) -> Result<serde_json::Value, ClientError> {
        let body = json!({ "newName": new_name });
        self.send_json(
            host,
            paired,
            reqwest::Method::PATCH,
            &iso_path(name)?,
            &body,
        )
        .await
    }

    /// GET /isos/{name}/inspection: what an image installs (OS and Windows editions).
    pub async fn inspect_iso(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        name: &str,
    ) -> Result<serde_json::Value, ClientError> {
        let path = format!("{}/inspection", iso_path(name)?);
        self.get_json(host, paired, &path).await
    }

    /// POST /unattend-profiles (no ID) or PUT /unattend-profiles/{id}. The host validates the profile.
    pub async fn save_unattend_profile(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        profile_id: Option<&str>,
        profile: &serde_json::Value,
    ) -> Result<serde_json::Value, ClientError> {
        let (method, path) = match profile_id {
            Some(id) => (reqwest::Method::PUT, profile_path(id)?),
            None => (reqwest::Method::POST, "/unattend-profiles".to_string()),
        };
        self.send_json(host, paired, method, &path, profile).await
    }

    /// DELETE /unattend-profiles/{id}.
    pub async fn delete_unattend_profile(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        profile_id: &str,
    ) -> Result<(), ClientError> {
        self.send_paired(
            host,
            paired,
            reqwest::Method::DELETE,
            &profile_path(profile_id)?,
        )
        .await
        .map(|_| ())
    }

    /// DELETE /isos/{name}.
    pub async fn delete_iso(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        name: &str,
    ) -> Result<(), ClientError> {
        self.send_paired(host, paired, reqwest::Method::DELETE, &iso_path(name)?)
            .await
            .map(|_| ())
    }

    /// PUT /isos/{name}: streams the file at `path` to the host's ISO library, calling `progress`
    /// with the bytes sent so far. Setting `cancel` stops the upload; the host then keeps nothing.
    /// Like other requests, another address is tried only when a connection could not be made.
    pub async fn upload_iso(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        name: &str,
        path: &std::path::Path,
        progress: Arc<dyn Fn(u64) + Send + Sync>,
        cancel: Arc<std::sync::atomic::AtomicBool>,
    ) -> Result<serde_json::Value, ClientError> {
        let resource = iso_path(name)?;
        let length = tokio::fs::metadata(path)
            .await
            .map_err(|e| ClientError::InvalidRequest(format!("the file could not be read: {e}")))?
            .len();
        let http = self.pinned_client(paired)?;
        let token = self.elevation_token(paired);

        let mut last_error = None;
        for base_url in self.ordered_candidates(host, paired) {
            let file = tokio::fs::File::open(path).await.map_err(|e| {
                ClientError::InvalidRequest(format!("the file could not be read: {e}"))
            })?;
            let mut request = http
                .put(format!("{base_url}{API_BASE_PATH}{resource}"))
                .header(reqwest::header::CONTENT_TYPE, "application/octet-stream")
                .header(reqwest::header::CONTENT_LENGTH, length)
                .body(reqwest::Body::wrap_stream(progress_stream(
                    file,
                    progress.clone(),
                    cancel.clone(),
                )))
                .timeout(UPLOAD_TIMEOUT);
            if let Some(token) = &token {
                request = request.header(ELEVATION_HEADER, token.as_str());
            }

            match request.send().await {
                Ok(response) => {
                    self.last_good
                        .lock()
                        .unwrap()
                        .insert(paired.host_id.clone(), base_url);
                    return parse(check(response).await?).await;
                }
                Err(_) if cancel.load(std::sync::atomic::Ordering::SeqCst) => {
                    return Err(ClientError::Cancelled)
                }
                Err(error) if tries_next_address(&error) => last_error = Some(error),
                Err(error) => return Err(unreachable(Some(error))),
            }
        }

        Err(unreachable(last_error))
    }

    fn elevation_token(&self, paired: &PairedHost) -> Option<zeroize::Zeroizing<String>> {
        self.elevations
            .lock()
            .unwrap()
            .get(&paired.host_id)
            .map(|elevation| elevation.token.clone())
    }

    /// The host's addresses, the one that last worked first.
    fn ordered_candidates(&self, host: &HostEntry, paired: &PairedHost) -> Vec<String> {
        let mut candidates = candidate_base_urls(host);
        if let Some(good) = self.last_good.lock().unwrap().get(&paired.host_id) {
            candidates.retain(|url| url != good);
            candidates.insert(0, good.clone());
        }
        candidates
    }

    async fn get_json(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        path: &str,
    ) -> Result<serde_json::Value, ClientError> {
        let response = self
            .send_paired(host, paired, reqwest::Method::GET, path)
            .await?;
        parse(response).await
    }

    async fn send_json(
        &self,
        host: &HostEntry,
        paired: &PairedHost,
        method: reqwest::Method,
        path: &str,
        body: &serde_json::Value,
    ) -> Result<serde_json::Value, ClientError> {
        let response = self
            .send_paired_with(host, paired, method, path, Some(body), None)
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
        let candidates = self.ordered_candidates(host, paired);
        let token = self.elevation_token(paired);

        let mut last_error = None;
        for base_url in candidates {
            let mut request =
                http.request(method.clone(), format!("{base_url}{API_BASE_PATH}{path}"));
            if let Some(body) = body {
                request = request.json(body);
            }
            if let Some(token) = &token {
                request = request.header(ELEVATION_HEADER, token.as_str());
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
                    let checked = check(response).await;
                    if let Err(error) = &checked {
                        // An expired or revoked token is useless; the frontend asks for the passphrase again.
                        if error.problem_code() == Some(ELEVATION_REQUIRED) {
                            self.elevations.lock().unwrap().remove(&paired.host_id);
                        }
                    }
                    return checked;
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

/// Read-only host-level resources the frontend can ask for by name.
#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum HostResource {
    Resources,
    Isos,
    Switches,
    UnattendProfiles,
}

impl HostResource {
    fn path(self) -> &'static str {
        match self {
            HostResource::Resources => "/host/resources",
            HostResource::Isos => "/isos",
            HostResource::Switches => "/switches",
            HostResource::UnattendProfiles => "/unattend-profiles",
        }
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

/// "/isos/{name}" with the name percent-encoded. Only plain .iso file names may become part of a path;
/// the host checks the name again.
/// "/unattend-profiles/{id}". Profile IDs are slugs or GUIDs; anything else never reaches the host.
fn profile_path(id: &str) -> Result<String, ClientError> {
    let valid = !id.is_empty()
        && id.len() <= 64
        && id
            .chars()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-');
    if valid {
        Ok(format!("/unattend-profiles/{id}"))
    } else {
        Err(ClientError::InvalidRequest(format!(
            "\"{id}\" is not a profile ID"
        )))
    }
}

fn iso_path(name: &str) -> Result<String, ClientError> {
    let valid = name.len() > 4
        && name.to_ascii_lowercase().ends_with(".iso")
        && !name.contains(['/', '\\', ':'])
        && !name.starts_with('.')
        && !name.chars().any(char::is_control);
    if !valid {
        return Err(ClientError::InvalidRequest(format!(
            "\"{name}\" is not an ISO file name"
        )));
    }

    let mut encoded = String::with_capacity(name.len());
    for byte in name.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            encoded.push(byte as char);
        } else {
            encoded.push_str(&format!("%{byte:02X}"));
        }
    }
    Ok(format!("/isos/{encoded}"))
}

/// The file as a stream of chunks that reports progress and stops with an error once cancelled.
fn progress_stream(
    file: tokio::fs::File,
    progress: Arc<dyn Fn(u64) + Send + Sync>,
    cancel: Arc<std::sync::atomic::AtomicBool>,
) -> impl futures_util::Stream<Item = std::io::Result<bytes::Bytes>> {
    use futures_util::StreamExt;
    let mut sent = 0u64;
    tokio_util::io::ReaderStream::with_capacity(file, UPLOAD_CHUNK_BYTES).map(move |chunk| {
        if cancel.load(std::sync::atomic::Ordering::SeqCst) {
            return Err(std::io::Error::new(
                std::io::ErrorKind::Interrupted,
                "the upload was cancelled",
            ));
        }
        let chunk = chunk?;
        sent += chunk.len() as u64;
        progress(sent);
        Ok(chunk)
    })
}

fn to_body<T: serde::Serialize>(value: &T) -> Result<serde_json::Value, ClientError> {
    serde_json::to_value(value).map_err(|e| ClientError::InvalidRequest(e.to_string()))
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
    let (code, issues) = match problem {
        Some(problem) => {
            let mut issues = problem.errors;
            issues.extend(problem.warnings);
            (problem.code, issues)
        }
        None => (None, Vec::new()),
    };

    Err(ClientError::Api {
        status: status.as_u16(),
        message,
        code,
        issues,
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
            matches!(&connect, Err(ClientError::Api { status: 409, message, .. }) if message == "Start the VM first."),
            "{connect:?}"
        );
        assert!(
            matches!(&list, Err(ClientError::Api { status: 503, message, .. }) if message == "Hyper-V unavailable"),
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
            Self::start_with(service_exe, None)
        }

        /// Starts the service with `passphrase` already set as its admin passphrase, as the tray would
        /// (PBKDF2-SHA256; one iteration is enough for a test and accepted from the data file).
        fn start_with(service_exe: &str, passphrase: Option<&str>) -> Self {
            let port = closed_address().port();
            let id = format!("{}-{port}", std::process::id());
            let pipe_name = format!("HyperHarbor.E2E.{id}");
            let data_directory = std::env::temp_dir().join(format!("hyperharbor-e2e-{id}"));
            if let Some(passphrase) = passphrase {
                use hmac::{Hmac, KeyInit, Mac};
                let salt = rand_bytes().repeat(2);
                let mut mac =
                    <Hmac<Sha256> as KeyInit>::new_from_slice(passphrase.as_bytes()).unwrap();
                mac.update(&salt);
                mac.update(&1u32.to_be_bytes());
                let hash = mac.finalize().into_bytes();
                std::fs::create_dir_all(&data_directory).unwrap();
                std::fs::write(
                    data_directory.join("admin-passphrase.json"),
                    json!({ "Salt": encode_base64(&salt), "Hash": encode_base64(&hash), "Iterations": 1 }).to_string(),
                )
                .unwrap();
            }
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

    /// Upload throughput of this client against the real host service on loopback (1 GB file):
    /// HH_E2E_SERVICE_EXE=<path to HyperHarbor.Host.Service.exe> cargo test --release upload_throughput -- --ignored --nocapture
    #[tokio::test(flavor = "multi_thread")]
    #[ignore]
    async fn upload_throughput() {
        let service_exe = std::env::var("HH_E2E_SERVICE_EXE").expect("HH_E2E_SERVICE_EXE");
        let service = LoopbackHost::start_with(&service_exe, Some("benchmark passphrase"));
        let pins = service.tray();
        let target = host(&["127.0.0.1"], service.port);
        let api = api();
        let pending = api.start_pairing(&target).await.unwrap();
        let pin = pins.recv_timeout(Duration::from_secs(10)).unwrap();
        let paired = api.complete_pairing(&target, &pending, &pin).await.unwrap();
        api.elevate(&target, &paired, "benchmark passphrase")
            .await
            .unwrap();

        let size = 1024 * 1024 * 1024;
        let path = std::env::temp_dir().join(format!("hh-bench-{}.iso", hex::encode(rand_bytes())));
        {
            use std::io::Write;
            let mut file = std::io::BufWriter::new(std::fs::File::create(&path).unwrap());
            let block = vec![0x5Au8; 4 * 1024 * 1024];
            for _ in 0..size / block.len() {
                file.write_all(&block).unwrap();
            }
        }

        let started = std::time::Instant::now();
        let result = api
            .upload_iso(
                &target,
                &paired,
                "bench.iso",
                &path,
                Arc::new(|_| {}),
                Arc::new(std::sync::atomic::AtomicBool::new(false)),
            )
            .await;
        let elapsed = started.elapsed().as_secs_f64();
        std::fs::remove_file(&path).unwrap();

        result.expect("upload");
        println!(
            "Uploaded 1 GB in {elapsed:.1} s: {:.0} MB/s",
            1024.0 / elapsed
        );
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

        // Lifecycle wiring in the real service: no passphrase is set, so elevated calls are refused
        // with elevationUnavailable before anything reaches Hyper-V.
        let elevation = api
            .elevation_status(&target, &paired)
            .await
            .expect("elevation status");
        assert_eq!(elevation["configured"], false);
        let turn_off = api
            .perform_vm_action(&target, &paired, VM_ID, "turnOff")
            .await;
        assert_eq!(
            turn_off.unwrap_err().problem_code(),
            Some("elevationUnavailable")
        );
        let isos = api
            .get_resource(&target, &paired, HostResource::Isos)
            .await
            .expect("ISO library");
        assert!(isos.is_array());
        match api
            .get_resource(&target, &paired, HostResource::Resources)
            .await
        {
            Ok(resources) => assert!(resources["logicalProcessorCount"].as_u64() > Some(0)),
            Err(ClientError::Api { status: 503, .. }) => {}
            Err(error) => panic!("host resources: {error:?}"),
        }
        let job = api
            .get_job(&target, &paired, "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d")
            .await;
        assert!(
            matches!(job, Err(ClientError::Api { status: 404, .. })),
            "{job:?}"
        );

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

        let console: crate::console::ConsoleSession =
            serde_json::from_value(fixture("ConsoleSession")).unwrap();
        assert_eq!(console.user_name, r"HOSTPC\hhc-owner");
        console.validate().unwrap();

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

    #[test]
    fn lifecycle_requests_round_trip_the_contract_fixtures() {
        for schema in ["VmDeleteRequest", "CreateVmRequest"] {
            let sample = fixture(schema);
            let body = match schema {
                "VmDeleteRequest" => {
                    to_body(&serde_json::from_value::<DeleteVmRequest>(sample.clone()).unwrap())
                }
                _ => to_body(&serde_json::from_value::<CreateVmRequest>(sample.clone()).unwrap()),
            }
            .unwrap();
            assert_eq!(body, sample, "{schema}");
        }

        // Unset compute fields are left out, so the host keeps those settings.
        let sample = fixture("UpdateVmComputeRequest");
        let update: UpdateComputeRequest = serde_json::from_value(sample.clone()).unwrap();
        assert_eq!(to_body(&update).unwrap(), sample);
        assert_eq!(
            to_body(&UpdateComputeRequest::default()).unwrap(),
            json!({ "shutDownToApply": false, "acknowledgeWarnings": false })
        );
    }

    #[test]
    fn lifecycle_responses_parse() {
        let job = fixture("VmJob");
        assert_eq!(job["kind"], "createVm");
        assert!(job["vmId"].is_null() && job["error"].is_null());
        let preview = fixture("VmDeletePreview");
        assert_eq!(preview["blockers"][0]["scope"], "deleteDisks");
        let update = fixture("VmComputeUpdate");
        assert!(update["job"].is_null());
        assert_eq!(update["settings"]["requiresOff"][0], "processorCount");
    }

    fn elevation_server(token: &'static str) -> TestServer {
        TestServer::start("127.0.0.1:0", move |request| {
            if request.path.ends_with("/auth/elevation") && request.method == "POST" {
                Reply::json(
                    200,
                    format!(r#"{{"token":"{token}","expiresAt":"2026-10-03T12:05:00Z"}}"#),
                )
            } else if request.header(ELEVATION_HEADER) == Some(token) {
                Reply::json(202, r#"{"accepted":true}"#)
            } else {
                Reply::json(
                    403,
                    r#"{"title":"Elevation required","status":403,"detail":"Enter the passphrase.","code":"elevationRequired"}"#,
                )
            }
        })
    }

    #[tokio::test]
    async fn elevation_token_is_kept_here_and_sent_with_later_requests() {
        let server = elevation_server("secret-token");
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();
        let paired = paired(server.certificate_hash());

        let before = api
            .perform_vm_action(&target, &paired, VM_ID, "turnOff")
            .await;
        let granted = api.elevate(&target, &paired, "passphrase").await.unwrap();
        let after = api
            .perform_vm_action(&target, &paired, VM_ID, "turnOff")
            .await;

        assert_eq!(before.unwrap_err().problem_code(), Some(ELEVATION_REQUIRED));
        assert!(after.is_ok(), "{after:?}");
        assert_eq!(granted.expires_at, "2026-10-03T12:05:00Z");
        let serialized = serde_json::to_string(&granted).unwrap();
        assert!(!serialized.contains("secret-token"), "{serialized}");
        let requests = server.requests();
        assert_eq!(requests[0].header(ELEVATION_HEADER), None);
        assert_eq!(requests[2].header(ELEVATION_HEADER), Some("secret-token"));
    }

    #[tokio::test]
    async fn rejected_token_is_forgotten() {
        let server = elevation_server("good-token");
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();
        let paired = paired(server.certificate_hash());
        api.elevations.lock().unwrap().insert(
            paired.host_id.clone(),
            Elevation {
                token: zeroize::Zeroizing::new("expired-token".into()),
            },
        );

        let first = api
            .perform_vm_action(&target, &paired, VM_ID, "turnOff")
            .await;
        let _ = api
            .perform_vm_action(&target, &paired, VM_ID, "turnOff")
            .await;

        assert!(first.is_err());
        let requests = server.requests();
        assert_eq!(requests[0].header(ELEVATION_HEADER), Some("expired-token"));
        assert_eq!(requests[1].header(ELEVATION_HEADER), None);
    }

    #[tokio::test]
    async fn problem_codes_and_issues_reach_the_frontend() {
        let server = TestServer::start("127.0.0.1:0", |_| {
            Reply::json(
                409,
                r#"{"title":"Check host resources","status":409,"code":"resourceWarnings","warnings":[{"field":"startupMemoryMb","message":"Low memory."}]}"#,
            )
        });
        let target = host(&["127.0.0.1"], server.address.port());
        let request = CreateVmRequest {
            name: "Dev".into(),
            iso_name: "win.iso".into(),
            disk_size_gb: 64,
            processor_count: 2,
            startup_memory_mb: 4096,
            maximum_memory_mb: 4096,
            dynamic_memory: false,
            switch_id: None,
            enable_tpm: true,
            acknowledge_warnings: false,
            install: None,
        };

        let result = api()
            .create_vm(&target, &paired(server.certificate_hash()), &request)
            .await;

        match result {
            Err(ClientError::Api {
                status,
                code,
                issues,
                ..
            }) => {
                assert_eq!(status, 409);
                assert_eq!(code.as_deref(), Some("resourceWarnings"));
                assert_eq!(issues[0].field, "startupMemoryMb");
            }
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn iso_names_are_encoded_and_paths_are_refused() {
        assert_eq!(
            iso_path("Win 11 (24H2).iso").unwrap(),
            "/isos/Win%2011%20%2824H2%29.iso"
        );
        assert_eq!(iso_path("ubuntu.ISO").unwrap(), "/isos/ubuntu.ISO");
        for name in [
            "../x.iso",
            "a\\b.iso",
            "C:x.iso",
            ".hidden.iso",
            "image.img",
            ".iso",
            "a\n.iso",
        ] {
            assert!(
                matches!(iso_path(name), Err(ClientError::InvalidRequest(_))),
                "{name}"
            );
        }
    }

    fn iso_file(bytes: usize) -> std::path::PathBuf {
        let path =
            std::env::temp_dir().join(format!("hh-upload-{}.iso", hex::encode(rand_bytes())));
        std::fs::write(&path, vec![7u8; bytes]).unwrap();
        path
    }

    fn rand_bytes() -> [u8; 8] {
        let mut bytes = [0u8; 8];
        getrandom::fill(&mut bytes).unwrap();
        bytes
    }

    #[tokio::test]
    async fn upload_streams_the_file_with_progress_and_elevation() {
        let server = TestServer::start("127.0.0.1:0", |request| {
            Reply::json(
                201,
                format!(
                    r#"{{"name":"debian.iso","sizeBytes":{},"modifiedAt":"2026-10-03T12:00:00Z","usedBy":[]}}"#,
                    request.body.len()
                ),
            )
        });
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();
        let paired = paired(server.certificate_hash());
        api.elevations.lock().unwrap().insert(
            paired.host_id.clone(),
            Elevation {
                token: zeroize::Zeroizing::new("token".into()),
            },
        );
        let path = iso_file(3 * UPLOAD_CHUNK_BYTES + 5);
        let reported = Arc::new(Mutex::new(Vec::new()));
        let sink = reported.clone();

        let image = api
            .upload_iso(
                &target,
                &paired,
                "debian.iso",
                &path,
                Arc::new(move |sent| sink.lock().unwrap().push(sent)),
                Arc::new(std::sync::atomic::AtomicBool::new(false)),
            )
            .await
            .unwrap();
        std::fs::remove_file(&path).unwrap();

        let total = (3 * UPLOAD_CHUNK_BYTES + 5) as u64;
        assert_eq!(image["sizeBytes"], total);
        assert_eq!(reported.lock().unwrap().last(), Some(&total));
        let request = &server.requests()[0];
        assert_eq!(request.method, "PUT");
        assert_eq!(request.path, "/api/v1/isos/debian.iso");
        assert_eq!(request.header(ELEVATION_HEADER), Some("token"));
        assert_eq!(
            request.header("content-type"),
            Some("application/octet-stream")
        );
    }

    #[tokio::test]
    async fn cancelled_upload_reports_cancelled() {
        let server = TestServer::start("127.0.0.1:0", |_| Reply::json(201, "{}"));
        let target = host(&["127.0.0.1"], server.address.port());
        let path = iso_file(2 * UPLOAD_CHUNK_BYTES);

        let result = api()
            .upload_iso(
                &target,
                &paired(server.certificate_hash()),
                "debian.iso",
                &path,
                Arc::new(|_| {}),
                Arc::new(std::sync::atomic::AtomicBool::new(true)),
            )
            .await;
        std::fs::remove_file(&path).unwrap();

        assert!(matches!(result, Err(ClientError::Cancelled)), "{result:?}");
    }

    #[tokio::test]
    async fn unknown_actions_and_job_ids_never_reach_the_host() {
        let server = TestServer::start("127.0.0.1:0", ok_list);
        let target = host(&["127.0.0.1"], server.address.port());
        let api = api();
        let paired = paired(server.certificate_hash());

        let action = api
            .perform_vm_action(&target, &paired, VM_ID, "explode")
            .await;
        let job = api.get_job(&target, &paired, "../vms").await;

        assert!(matches!(action, Err(ClientError::InvalidRequest(_))));
        assert!(matches!(job, Err(ClientError::InvalidRequest(_))));
        assert!(server.requests().is_empty());
    }
    fn console_server() -> TestServer {
        TestServer::start("127.0.0.1:0", |request| {
            let upgrade = request
                .header("connection")
                .is_some_and(|value| value.eq_ignore_ascii_case("upgrade"))
                && request.header("upgrade") == Some("hyperharbor-console");
            if request.path.ends_with("/console/tunnel")
                && upgrade
                && request.header("x-hyperharbor-console-ticket") == Some("ticket-1")
            {
                Reply::upgrade_and_echo()
            } else {
                Reply::json(403, r#"{"title":"Console ticket rejected","status":403}"#)
            }
        })
    }

    async fn echo_round_trip<S>(stream: &mut S, payload: &[u8])
    where
        S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin,
    {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        stream.write_all(payload).await.unwrap();
        stream.flush().await.unwrap();
        let mut echoed = vec![0; payload.len()];
        tokio::time::timeout(Duration::from_secs(10), stream.read_exact(&mut echoed))
            .await
            .expect("echo timed out")
            .unwrap();
        assert_eq!(echoed, payload);
    }

    #[tokio::test]
    async fn console_tunnel_upgrades_and_carries_bytes() {
        let server = console_server();
        let target = host(&["127.0.0.1"], server.address.port());

        let mut tunnel = api()
            .open_console_tunnel(
                &target,
                &paired(server.certificate_hash()),
                VM_ID,
                "ticket-1",
            )
            .await
            .expect("tunnel");

        echo_round_trip(&mut tunnel, b"remote desktop bytes").await;
        let request = &server.requests()[0];
        assert_eq!(request.method, "POST");
        assert_eq!(request.path, format!("/api/v1/vms/{VM_ID}/console/tunnel"));
    }

    #[tokio::test]
    async fn console_tunnel_rejection_is_an_api_error() {
        let server = console_server();
        let target = host(&["127.0.0.1"], server.address.port());

        let result = api()
            .open_console_tunnel(&target, &paired(server.certificate_hash()), VM_ID, "wrong")
            .await;

        assert!(
            matches!(result, Err(ClientError::Api { status: 403, .. })),
            "{:?}",
            result.err()
        );
    }

    #[tokio::test]
    async fn console_listener_tunnels_every_connection_until_stopped() {
        let server = console_server();
        let target = host(&["127.0.0.1"], server.address.port());
        let listener = tokio::net::TcpListener::bind(("127.0.0.1", 0))
            .await
            .unwrap();
        let local = listener.local_addr().unwrap();
        let (stop, stopped) = tokio::sync::oneshot::channel();
        let serving = tokio::spawn(crate::console::serve(
            listener,
            stopped,
            Arc::new(api()),
            target,
            paired(server.certificate_hash()),
            VM_ID.into(),
            zeroize::Zeroizing::new("ticket-1".into()),
        ));

        let mut first = tokio::net::TcpStream::connect(local).await.unwrap();
        let mut second = tokio::net::TcpStream::connect(local).await.unwrap();
        echo_round_trip(&mut first, b"first connection").await;
        echo_round_trip(&mut second, b"second connection").await;

        // Stopping closes the listener; tunnels already open keep working.
        stop.send(()).unwrap();
        serving.await.unwrap();
        assert!(tokio::net::TcpStream::connect(local).await.is_err());
        echo_round_trip(&mut first, b"still open").await;
    }

    /// The client-wide request timeout (10 seconds) must not end an idle tunnel. Slow, so ignored;
    /// run it after changing how tunnels are requested.
    #[tokio::test]
    #[ignore]
    async fn console_tunnel_outlives_the_request_timeout() {
        let server = console_server();
        let target = host(&["127.0.0.1"], server.address.port());
        let mut tunnel = api()
            .open_console_tunnel(
                &target,
                &paired(server.certificate_hash()),
                VM_ID,
                "ticket-1",
            )
            .await
            .expect("tunnel");

        tokio::time::sleep(CONSOLE_TUNNEL_TIMEOUT + Duration::from_secs(2)).await;

        echo_round_trip(&mut tunnel, b"after idling").await;
    }
    #[tokio::test]
    async fn profiles_are_created_with_post_and_replaced_with_put() {
        let server = TestServer::start("127.0.0.1:0", |_| Reply::json(200, r#"{"id":"p"}"#));
        let target = host(&["127.0.0.1"], server.address.port());
        let pinned = paired(server.certificate_hash());
        let profile = json!({ "name": "Build", "os": "linux" });

        api()
            .save_unattend_profile(&target, &pinned, None, &profile)
            .await
            .unwrap();
        api()
            .save_unattend_profile(&target, &pinned, Some("ubuntu-dev-server"), &profile)
            .await
            .unwrap();
        let rejected = api()
            .save_unattend_profile(&target, &pinned, Some("../vms"), &profile)
            .await;

        let requests = server.requests();
        assert_eq!(requests.len(), 2);
        assert_eq!(
            (requests[0].method.as_str(), requests[0].path.as_str()),
            ("POST", "/api/v1/unattend-profiles")
        );
        assert_eq!(
            (requests[1].method.as_str(), requests[1].path.as_str()),
            ("PUT", "/api/v1/unattend-profiles/ubuntu-dev-server")
        );
        assert!(matches!(rejected, Err(ClientError::InvalidRequest(_))));
    }

    #[tokio::test]
    async fn iso_inspection_uses_the_encoded_image_name() {
        let server = TestServer::start("127.0.0.1:0", |_| {
            Reply::json(
                200,
                r#"{"os":"windows","distribution":"Windows","editions":[]}"#,
            )
        });
        let target = host(&["127.0.0.1"], server.address.port());

        api()
            .inspect_iso(&target, &paired(server.certificate_hash()), "Win 11.iso")
            .await
            .unwrap();

        assert_eq!(
            server.requests()[0].path,
            "/api/v1/isos/Win%2011.iso/inspection"
        );
    }
}
