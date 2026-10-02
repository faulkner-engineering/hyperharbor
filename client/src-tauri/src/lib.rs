mod api;
mod discovery;
mod error;
mod hosts;
mod identity;
mod paired;
mod spake2;
mod tls;

use std::collections::HashMap;
use std::sync::Arc;

use serde::Serialize;
use tauri::{Emitter, Manager, State};

use crate::api::{ApiClient, PendingPairing};
use crate::error::ClientError;
use crate::hosts::{HostEntry, HostRegistry};
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

impl AppState {
    fn host(&self, key: &str) -> Result<HostEntry, ClientError> {
        let mut host = self.hosts.get(key).ok_or(ClientError::UnknownHost)?;
        host.paired = self.pairing_for(&host).is_some();
        Ok(host)
    }

    fn pairing_for(&self, host: &HostEntry) -> Option<PairedHost> {
        self.paired.find(host.host_id.as_deref(), &host.key)
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct PairingStarted {
    pairing_id: String,
    expires_at: String,
}

#[tauri::command]
fn list_hosts(state: State<'_, AppState>) -> Vec<HostEntry> {
    let mut hosts = state.hosts.list();
    for host in &mut hosts {
        host.paired = state.pairing_for(host).is_some();
    }
    hosts
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

#[tauri::command]
async fn list_vms(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let host = state.host(&key)?;
    let paired = state
        .pairing_for(&host)
        .ok_or(ClientError::PairingRequired)?;
    state.api.list_vms(&host, &paired).await
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

#[tauri::command]
async fn cancel_pairing(state: State<'_, AppState>, key: String) -> Result<(), ClientError> {
    state.pending.lock().await.remove(&key);
    Ok(())
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
            unpair
        ])
        .run(tauri::generate_context!())
        .expect("error while running HyperHarbor client");
}
