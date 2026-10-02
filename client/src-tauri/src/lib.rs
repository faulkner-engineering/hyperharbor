mod api;
mod discovery;
mod error;
mod hosts;
mod identity;
mod paired;
mod rdp;
mod spake2;
mod tls;
mod wake;

use std::collections::HashMap;
use std::sync::Arc;

use serde::Serialize;
use tauri::{Emitter, Manager, State};

use crate::api::{ApiClient, PendingPairing};
use crate::error::ClientError;
use crate::hosts::{HostEntry, HostRegistry, HostSource};
use crate::identity::ClientIdentity;
use crate::paired::{PairedHost, PairedHostStore};

/// Event emitted to the frontend whenever the host list changes.
const HOSTS_CHANGED_EVENT: &str = "hosts-changed";

struct AppState {
    hosts: Arc<HostRegistry>,
    paired: PairedHostStore,
    api: ApiClient,
    pending: tokio::sync::Mutex<HashMap<String, PendingPairing>>,
    // Kept alive so mDNS browsing continues for the lifetime of the app.
    _mdns: Option<mdns_sd::ServiceDaemon>,
}

/// Wake details are refreshed at most this often while a host is reachable.
const WAKE_REFRESH_SECONDS: u64 = 60 * 60;

impl AppState {
    /// Discovered and manual hosts, plus paired hosts that are not currently discovered.
    /// Also records the latest discovered address of each paired host, so a host that goes to
    /// sleep before it was ever selected is still listed with a usable address.
    fn all_hosts(&self) -> Vec<HostEntry> {
        let mut hosts = self.hosts.list();
        for host in &mut hosts {
            self.annotate(host);
            if host.paired && host.source == HostSource::Discovered {
                if let Some(paired) = self.pairing_for(host) {
                    // Best effort: a failed write only means the stored address is older.
                    let _ = self.paired.update_endpoint(&paired.host_id, host);
                }
            }
        }
        paired::add_remembered(&mut hosts, &self.paired.all(), self.hosts.local_host_name());
        hosts
    }

    fn host(&self, key: &str) -> Result<HostEntry, ClientError> {
        self.all_hosts()
            .into_iter()
            .find(|host| host.key == key)
            .ok_or(ClientError::UnknownHost)
    }

    fn annotate(&self, host: &mut HostEntry) {
        let paired = self.pairing_for(host);
        host.paired = paired.is_some();
        host.can_wake = paired.is_some_and(|p| !p.wake_adapters.is_empty());
    }

    fn pairing_for(&self, host: &HostEntry) -> Option<PairedHost> {
        self.paired.find(host.host_id.as_deref(), &host.key)
    }

    fn paired_host(&self, key: &str) -> Result<(HostEntry, PairedHost), ClientError> {
        let host = self.host(key)?;
        let paired = self
            .pairing_for(&host)
            .ok_or(ClientError::PairingRequired)?;
        Ok((host, paired))
    }
}

fn now_seconds() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_secs())
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct PairingStarted {
    pairing_id: String,
    expires_at: String,
}

#[tauri::command]
fn list_hosts(state: State<'_, AppState>) -> Vec<HostEntry> {
    state.all_hosts()
}

#[tauri::command]
fn add_manual_host(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    address: String,
) -> Result<HostEntry, ClientError> {
    let entry = state.hosts.add_manual(&address)?;
    let _ = app.emit(HOSTS_CHANGED_EVENT, ());
    Ok(entry)
}

#[tauri::command]
fn remove_manual_host(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    key: String,
) -> Result<(), ClientError> {
    state.hosts.remove_manual(&key)?;
    let _ = app.emit(HOSTS_CHANGED_EVENT, ());
    Ok(())
}

/// Lists VMs and, while the host is reachable, keeps its Wake-on-LAN details fresh so it can be
/// woken later.
#[tauri::command]
async fn list_vms(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    let vms = state.api.list_vms(&host, &paired).await?;

    // Remember where the host was found so it stays listed while asleep.
    if host.source != HostSource::Remembered {
        state.paired.update_endpoint(&paired.host_id, &host)?;
    }

    let stale = paired
        .wake_refreshed_at
        .is_none_or(|at| now_seconds().saturating_sub(at) > WAKE_REFRESH_SECONDS);
    if stale {
        if let Ok(adapters) = state.api.wake_info(&host, &paired).await {
            let first_time = paired.wake_adapters.is_empty() && !adapters.is_empty();
            state
                .paired
                .update_wake(&paired.host_id, adapters, now_seconds())?;
            if first_time {
                let _ = app.emit(HOSTS_CHANGED_EVENT, ());
            }
        }
    }

    Ok(vms)
}

/// One-time setup of this User's account on a VM, using a guest administrator credential.
#[tauri::command]
async fn provision_vm(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    admin_user_name: String,
    mut admin_password: String,
    enable_remote_desktop: bool,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    let result = state
        .api
        .provision_vm(
            &host,
            &paired,
            &vm_id,
            &admin_user_name,
            &admin_password,
            enable_remote_desktop,
        )
        .await;
    zeroize::Zeroize::zeroize(&mut admin_password);
    result
}

/// Opens Remote Desktop to a provisioned VM: checks reachability first (so no password is rotated
/// for a VM this device cannot reach), requests credentials, and launches mstsc. The credential is
/// removed from the OS store once the session opens.
#[tauri::command]
async fn connect_vm(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    address: String,
) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;

    let probe_address = address.clone();
    let reachable =
        tauri::async_runtime::spawn_blocking(move || rdp::is_reachable(&probe_address, 3389))
            .await
            .unwrap_or(false);
    if !reachable {
        return Err(ClientError::VmUnreachable(address));
    }

    let connection = state.api.connect_vm(&host, &paired, &vm_id).await?;
    rdp::launch(&connection, &rdp::file_directory())
}

/// Sends Wake-on-LAN magic packets using the cached adapter details. Returns datagrams sent.
#[tauri::command]
fn wake_host(state: State<'_, AppState>, key: String) -> Result<usize, ClientError> {
    let (_, paired) = state.paired_host(&key)?;
    wake::wake(&paired.wake_adapters)
}

#[tauri::command]
async fn get_wake_readiness(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.wake_readiness(&host, &paired).await
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct WakeFixOutcome {
    /// "applied" or "awaitingApproval".
    status: &'static str,
    readiness: Option<serde_json::Value>,
}

#[tauri::command]
async fn fix_wake(
    state: State<'_, AppState>,
    key: String,
    check_ids: Vec<String>,
) -> Result<WakeFixOutcome, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    let readiness = state.api.fix_wake(&host, &paired, &check_ids).await?;
    Ok(WakeFixOutcome {
        status: if readiness.is_some() {
            "applied"
        } else {
            "awaitingApproval"
        },
        readiness,
    })
}

#[tauri::command]
async fn start_wake_test(
    state: State<'_, AppState>,
    key: String,
    delay_seconds: u32,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .start_wake_test(&host, &paired, delay_seconds)
        .await
}

/// Asks the host to show a PIN. The pending exchange is kept until `complete_pairing`.
#[tauri::command]
async fn start_pairing(
    state: State<'_, AppState>,
    key: String,
) -> Result<PairingStarted, ClientError> {
    let host = state.host(&key)?;
    let pending = state.api.start_pairing(&host).await?;
    let started = PairingStarted {
        pairing_id: pending.pairing_id.clone(),
        expires_at: pending.expires_at.clone(),
    };
    state.pending.lock().await.insert(key, pending);
    Ok(started)
}

#[tauri::command]
async fn complete_pairing(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    key: String,
    pin: String,
) -> Result<HostEntry, ClientError> {
    let host = state.host(&key)?;
    let mut pending = state.pending.lock().await;
    let request = pending.get(&key).ok_or(ClientError::NoPendingPairing)?;

    match state.api.complete_pairing(&host, request, pin.trim()).await {
        Ok(mut paired) => {
            pending.remove(&key);
            if let Some(existing) = state.paired.find(Some(&paired.host_id), &key) {
                paired
                    .entry_keys
                    .extend(existing.entry_keys.into_iter().filter(|k| k != &key));
            }
            state.paired.save(paired)?;
            let _ = app.emit(HOSTS_CHANGED_EVENT, ());
            state.host(&key)
        }
        Err(error) => {
            // A wrong PIN can be retried; anything else ends this attempt.
            if !matches!(error, ClientError::Api { status: 401, .. }) {
                pending.remove(&key);
            }
            Err(error)
        }
    }
}

/// Forgets the pending exchange locally and asks the host to end the request.
#[tauri::command]
async fn cancel_pairing(state: State<'_, AppState>, key: String) -> Result<(), ClientError> {
    let pending = state.pending.lock().await.remove(&key);
    match pending {
        Some(pending) => state.api.cancel_pairing(&pending).await,
        None => Ok(()),
    }
}

/// Removes the pairing on the host when reachable, and always forgets it locally.
#[tauri::command]
async fn unpair(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    key: String,
) -> Result<(), ClientError> {
    let host = state.host(&key)?;
    let paired = state
        .pairing_for(&host)
        .ok_or(ClientError::PairingRequired)?;
    let remote = state.api.unpair(&host, &paired).await;
    state.paired.remove(&paired.host_id)?;
    let _ = app.emit(HOSTS_CHANGED_EVENT, ());
    remote
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            // Remove any temporary Remote Desktop credential left by a previous run.
            rdp::remove_stale_credentials();

            let config_dir = app.path().app_config_dir()?;
            let local_host_name = gethostname::gethostname().to_string_lossy().into_owned();

            let identity = Arc::new(ClientIdentity::load_or_create(
                &config_dir,
                &local_host_name,
            )?);
            let hosts = Arc::new(HostRegistry::new(
                local_host_name.clone(),
                Some(config_dir.join("hosts.json")),
            ));

            let handle = app.handle().clone();
            let mdns = match discovery::start(hosts.clone(), move || {
                let _ = handle.emit(HOSTS_CHANGED_EVENT, ());
            }) {
                Ok(daemon) => Some(daemon),
                Err(error) => {
                    eprintln!("mDNS discovery is unavailable: {error}");
                    None
                }
            };

            app.manage(AppState {
                hosts,
                paired: PairedHostStore::new(Some(config_dir.join("paired-hosts.json"))),
                api: ApiClient::new(identity, local_host_name),
                pending: tokio::sync::Mutex::new(HashMap::new()),
                _mdns: mdns,
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            list_hosts,
            add_manual_host,
            remove_manual_host,
            list_vms,
            start_pairing,
            complete_pairing,
            cancel_pairing,
            unpair,
            wake_host,
            get_wake_readiness,
            fix_wake,
            start_wake_test,
            provision_vm,
            connect_vm
        ])
        .run(tauri::generate_context!())
        .expect("error while running HyperHarbor client");
}
