mod api;
mod discovery;
mod error;
mod hosts;
mod spake2;

use std::sync::Arc;

use tauri::{Emitter, Manager, State};

use crate::api::ApiClient;
use crate::error::ClientError;
use crate::hosts::{HostEntry, HostRegistry};

/// Event emitted to the frontend whenever the host list changes.
const HOSTS_CHANGED_EVENT: &str = "hosts-changed";

struct AppState {
    hosts: Arc<HostRegistry>,
    api: ApiClient,
    // Kept alive so mDNS browsing continues for the lifetime of the app.
    _mdns: Option<mdns_sd::ServiceDaemon>,
}

#[tauri::command]
fn list_hosts(state: State<'_, AppState>) -> Vec<HostEntry> {
    state.hosts.list()
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
    let host = state.hosts.get(&key).ok_or(ClientError::UnknownHost)?;
    state.api.list_vms(&host).await
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            let storage_path = app
                .path()
                .app_config_dir()
                .ok()
                .map(|dir| dir.join("hosts.json"));
            let local_host_name = gethostname::gethostname().to_string_lossy().into_owned();
            let hosts = Arc::new(HostRegistry::new(local_host_name, storage_path));

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
                api: ApiClient::new(),
                _mdns: mdns,
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            list_hosts,
            add_manual_host,
            remove_manual_host,
            list_vms
        ])
        .run(tauri::generate_context!())
        .expect("error while running HyperHarbor client");
}
