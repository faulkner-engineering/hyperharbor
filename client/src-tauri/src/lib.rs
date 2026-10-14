mod api;
mod camera;
mod console;
mod discovery;
mod error;
mod hosts;
mod identity;
mod monitors;
mod paired;
mod rdp;
mod sessions;
mod spake2;
mod tailnet;
#[cfg(test)]
mod test_server;
mod tls;
mod update;
mod wake;

use std::collections::HashMap;
use std::sync::Arc;

use serde::Serialize;
use tauri::{Emitter, Manager, State};

use crate::api::{ApiClient, PendingPairing};
use crate::error::ClientError;
use crate::hosts::{HostEntry, HostRegistry, HostSource};
use crate::identity::ClientIdentity;
use crate::monitors::{Monitor, MonitorChoice, MonitorChoiceStore};
use crate::paired::{PairedHost, PairedHostStore};

/// Event emitted to the frontend whenever the host list changes.
const HOSTS_CHANGED_EVENT: &str = "hosts-changed";

struct AppState {
    hosts: Arc<HostRegistry>,
    paired: PairedHostStore,
    api: Arc<ApiClient>,
    pending: tokio::sync::Mutex<HashMap<String, PendingPairing>>,
    /// Files chosen in the ISO picker, by the ID the frontend gets instead of a path.
    picked_isos: std::sync::Mutex<HashMap<String, std::path::PathBuf>>,
    /// Cancel flags of running uploads, by the same ID.
    uploads: std::sync::Mutex<HashMap<String, Arc<std::sync::atomic::AtomicBool>>>,
    /// Which monitors each VM's Remote Desktop sessions use.
    monitors: MonitorChoiceStore,
    /// Remote Desktop and console windows still open, by host; those hosts get a check-in each minute.
    sessions: sessions::ActiveSessions,
    /// One physical camera shared with every open session through a virtual camera each.
    camera: Arc<camera::service::CameraService>,
    /// Updates of this program (the host updates itself through its own API).
    updates: update::service::UpdateService,
    /// Tailscale on this PC and the hosts found on the tailnet.
    tailnet: Arc<tailnet::TailnetService>,
    // Kept alive so mDNS browsing continues for the lifetime of the app.
    _mdns: Option<mdns_sd::ServiceDaemon>,
}

/// How often this PC looks at its tailnet for hosts.
const TAILNET_REFRESH_INTERVAL: std::time::Duration = std::time::Duration::from_secs(30);

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

        // Hosts found on the tailnet join the entry that is already listed, so one host appears once with both
        // addresses; a paired host reachable only over the tailnet is listed too.
        let paired = self.paired.all();
        tailnet::merge_tailnet(
            &mut hosts,
            &self.tailnet.snapshot().hosts,
            &paired,
            self.hosts.local_host_name(),
        );
        for host in &mut hosts {
            if host.tailscale.is_some() {
                self.annotate(host);
                if host.paired && host.source != HostSource::Manual {
                    if let Some(pairing) = self.pairing_for(host) {
                        // Best effort, like the discovered address above: keeps the Tailscale address for later.
                        let _ = self.paired.update_endpoint(&pairing.host_id, host);
                    }
                }
            }
        }
        paired::add_remembered(&mut hosts, &paired, self.hosts.local_host_name());
        hosts
    }

    /// Reads Tailscale on this PC and probes its peers. Returns true when the host list may have changed.
    async fn refresh_tailnet(&self) -> bool {
        let port = self.tailnet.settings.get().local_api_port;
        self.tailnet
            .refresh_with(
                &tailnet::HttpSource { port },
                &tailnet::CliSource,
                self.api.as_ref(),
                &self.paired.all(),
                std::time::Instant::now(),
            )
            .await
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
    options: api::ProvisionOptions,
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
            options,
        )
        .await;
    zeroize::Zeroize::zeroize(&mut admin_password);
    result
}

/// Opens Remote Desktop to a provisioned VM: checks reachability first (so no password is rotated
/// for a VM this device cannot reach), requests credentials, and launches mstsc. The credential is
/// removed from the OS store once the session opens.
///
/// Windows guests also get the camera: on Windows 11 a virtual camera of their own (so several VMs
/// can show video at once), otherwise the physical camera for one VM at a time. `vm_name` names the
/// virtual camera. The result is something to tell the user (camera sharing unavailable, say), if any.
#[tauri::command]
async fn connect_vm(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    address: String,
    vm_name: Option<String>,
) -> Result<Option<String>, ClientError> {
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
    connection.validate()?;

    // The VM may report a different address by now; launch only to an address this device reaches.
    if connection.address != address {
        let probe_address = connection.address.clone();
        let port = connection.port;
        let reachable =
            tauri::async_runtime::spawn_blocking(move || rdp::is_reachable(&probe_address, port))
                .await
                .unwrap_or(false);
        if !reachable {
            return Err(ClientError::VmUnreachable(connection.address.clone()));
        }
    }
    let layout = state
        .monitors
        .get(&paired.host_id, &vm_id)
        .resolve(&monitors::list());
    // Linux guests (xrdp) have no camera redirection, so no virtual camera is made for them.
    let camera_key = format!("{}/{}", paired.host_id, vm_id);
    let prepared = if connection.guest_os == rdp::GuestOs::Linux {
        camera::service::Prepared {
            redirect: camera::CameraRedirect::None,
            notice: None,
        }
    } else {
        let service = state.camera.clone();
        let (prepare_key, name) = (camera_key.clone(), vm_name.unwrap_or_else(|| "VM".into()));
        // Creating a virtual camera takes a moment and talks to Windows, so not on the async runtime.
        tauri::async_runtime::spawn_blocking(move || service.prepare(&prepare_key, &name))
            .await
            .map_err(|e| ClientError::RdpFailed(e.to_string()))?
    };
    let session = state.sessions.begin(&key);
    let service = state.camera.clone();
    let end_key = camera_key.clone();
    let launched = rdp::launch(
        &connection,
        &layout,
        &prepared.redirect,
        &rdp::file_directory(),
        Some(Box::new(move || {
            service.end(&end_key);
            drop(session);
        })),
    );
    match launched {
        Ok(pid) => {
            state.camera.attach_process(&camera_key, pid);
            Ok(prepared.notice)
        }
        Err(error) => {
            state.camera.end(&camera_key);
            Err(error)
        }
    }
}

fn vm_camera_key(state: &AppState, key: &str, vm_id: &str) -> Result<String, ClientError> {
    let (_, paired) = state.paired_host(key)?;
    Ok(format!("{}/{}", paired.host_id, vm_id))
}

/// Runs the elevated camera setup when this process was started for it (see `camera::setup`);
/// returns the exit code, or None for a normal start.
pub fn run_camera_setup_helper() -> Option<i32> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    camera::setup::parse_args(&args).map(|command| camera::setup_windows::run_helper(&command))
}

/// Installs the camera source so virtual cameras can be created: copies the DLL that ships with the
/// program under Program Files and registers it, in an elevated copy of this program (the user
/// answers one UAC prompt). Needed once per device.
#[tauri::command]
async fn setup_camera_sharing(app: tauri::AppHandle) -> Result<(), ClientError> {
    let resource_dir = app
        .path()
        .resource_dir()
        .map_err(|e| ClientError::RdpFailed(e.to_string()))?;
    let dll = camera::setup_windows::bundled_dll(&resource_dir);
    if !dll.is_file() {
        return Err(ClientError::RdpFailed(
            "This copy of HyperHarbor does not include the camera source. Install it with the installer.".into(),
        ));
    }
    tauri::async_runtime::spawn_blocking(move || {
        let result = std::env::temp_dir().join(format!(
            "hyperharbor-camera-setup-{}.txt",
            std::process::id()
        ));
        let outcome = camera::setup_windows::run_elevated(
            &camera::setup_windows::install_args(&dll, &result),
            &result,
        );
        let _ = std::fs::remove_file(&result);
        outcome
    })
    .await
    .map_err(|e| ClientError::RdpFailed(e.to_string()))?
    .map_err(ClientError::RdpFailed)
}

/// True when the camera source installed under Program Files differs from the one that ships with
/// this program, as after a client update. Setting sharing up again (one UAC prompt) replaces it.
/// False when either copy cannot be found: there is nothing to update then.
#[tauri::command]
fn camera_source_outdated(app: tauri::AppHandle) -> bool {
    let Ok(resource_dir) = app.path().resource_dir() else {
        return false;
    };
    let Some(installed) = camera::setup_windows::registered_server_path() else {
        return false;
    };
    camera::setup::source_is_current(
        &installed,
        &camera::setup_windows::bundled_dll(&resource_dir),
    ) == Some(false)
}

/// Removes the camera source again (one UAC prompt): unregisters it and deletes its folder under
/// Program Files. Uninstalling HyperHarbor cannot do this itself, because that uninstaller is not
/// elevated. Open sessions lose their virtual camera, since the Windows camera service is stopped.
#[tauri::command]
async fn remove_camera_sharing() -> Result<(), ClientError> {
    tauri::async_runtime::spawn_blocking(|| {
        let result = std::env::temp_dir().join(format!(
            "hyperharbor-camera-remove-{}.txt",
            std::process::id()
        ));
        let args = vec!["--remove-camera".to_string(), result.display().to_string()];
        let outcome = camera::setup_windows::run_elevated(&args, &result);
        let _ = std::fs::remove_file(&result);
        outcome
    })
    .await
    .map_err(|e| ClientError::RdpFailed(e.to_string()))?
    .map_err(ClientError::RdpFailed)
}

/// The event the page listens to for changes in the update status.
const CLIENT_UPDATE_EVENT: &str = "client-update-changed";

/// A new version of this program is checked at start and then once a day.
const UPDATE_CHECK_INTERVAL: std::time::Duration = std::time::Duration::from_secs(24 * 60 * 60);

/// Waits before the first automatic check, so it does not compete with start-up work.
const UPDATE_FIRST_CHECK_DELAY: std::time::Duration = std::time::Duration::from_secs(15);

#[tauri::command]
fn get_client_update(state: State<'_, AppState>) -> update::service::UpdateStatus {
    state.updates.status()
}

/// Looks for a newer version of this program on the chosen channel. A failure is part of the status.
#[tauri::command]
async fn check_client_update(
    state: State<'_, AppState>,
) -> Result<update::service::UpdateStatus, ClientError> {
    Ok(state.updates.check().await)
}

/// Downloads and installs the version the last check found. The installer closes the program and
/// starts the new version. Open Remote Desktop and console windows end with it, so the page asks
/// first and sends `confirmed`.
#[tauri::command]
async fn install_client_update(
    state: State<'_, AppState>,
    confirmed: bool,
) -> Result<(), ClientError> {
    state
        .updates
        .install(state.sessions.keys().len(), confirmed)
        .await
}

#[tauri::command]
fn get_update_settings(state: State<'_, AppState>) -> update::settings::UpdateSettings {
    state.updates.settings()
}

#[tauri::command]
fn set_update_settings(
    state: State<'_, AppState>,
    settings: update::settings::UpdateSettings,
) -> Result<update::service::UpdateStatus, ClientError> {
    state.updates.set_settings(settings)
}

/// Whether camera sharing works on this device, the physical camera's state, and each session.
#[tauri::command]
fn get_camera_status(state: State<'_, AppState>) -> camera::service::CameraStatus {
    state.camera.status()
}

/// A VM's camera settings: whether the camera is shared with it, and what it sees while its window
/// is not in front.
#[tauri::command]
fn get_camera_prefs(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<camera::service::CameraPrefs, ClientError> {
    Ok(state.camera.prefs(&vm_camera_key(&state, &key, &vm_id)?))
}

#[tauri::command]
fn set_camera_prefs(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    prefs: camera::service::CameraPrefs,
) -> Result<(), ClientError> {
    state
        .camera
        .set_prefs(&vm_camera_key(&state, &key, &vm_id)?, prefs)
        .map_err(|e| ClientError::RdpFailed(e.to_string()))
}

/// Whether a VM's privacy shutter is closed.
#[tauri::command]
fn get_camera_shutter(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<bool, ClientError> {
    Ok(state
        .camera
        .shutter_closed(&vm_camera_key(&state, &key, &vm_id)?))
}

/// Closes (true) or opens (false) a VM's privacy shutter: a closed shutter shows the VM black.
#[tauri::command]
fn set_camera_shutter(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    closed: bool,
) -> Result<(), ClientError> {
    state
        .camera
        .set_shutter(&vm_camera_key(&state, &key, &vm_id)?, closed);
    Ok(())
}

/// The fixed ID under which the host's own Remote Desktop session keeps its monitor choice.
const HOST_SESSION_ID: &str = "host";

/// Opens Remote Desktop to the host itself for maintenance. mstsc asks for the host's Windows
/// account; HyperHarbor sends and stores no host credentials.
#[tauri::command]
async fn connect_host(state: State<'_, AppState>, key: String) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    let remote_desktop = state.api.host_remote_desktop(&host, &paired).await?;
    if !remote_desktop.supported || !remote_desktop.enabled {
        return Err(ClientError::RdpFailed(
            "Remote Desktop is off on the host.".into(),
        ));
    }

    let port = remote_desktop.port;
    let mut candidates: Vec<String> = Vec::new();
    for address in host.addresses.iter().chain(paired.addresses.iter()) {
        if rdp::is_valid_host_address(address) && !candidates.contains(address) {
            candidates.push(address.clone());
        }
    }
    // IPv4 first: link-local IPv6 addresses carry a zone that mstsc cannot use.
    candidates.sort_by_key(|address| address.parse::<std::net::Ipv4Addr>().is_err());

    let probe = candidates.clone();
    let reachable = tauri::async_runtime::spawn_blocking(move || {
        probe
            .into_iter()
            .find(|address| rdp::is_reachable(address, port))
    })
    .await
    .unwrap_or(None);
    let Some(address) = reachable else {
        return Err(ClientError::RdpFailed(format!(
            "this device cannot reach {} on port {port}. Check that the host's firewall allows Remote Desktop on this network.",
            candidates.first().map_or(host.display_name.as_str(), String::as_str)
        )));
    };

    let layout = state
        .monitors
        .get(&paired.host_id, HOST_SESSION_ID)
        .resolve(&monitors::list());
    let session = state.sessions.begin(&key);
    rdp::launch_host(
        &address,
        port,
        &layout,
        &rdp::file_directory(),
        Some(Box::new(move || drop(session))),
    )
}

/// Allows Remote Desktop connections to the host (needs elevation). Returns the HostRemoteDesktop.
#[tauri::command]
async fn enable_host_remote_desktop(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.enable_host_remote_desktop(&host, &paired).await
}

/// Downloads the host's log files as a zip (needs elevation) and saves it where the user chooses.
/// Returns the path, or None when the user cancels the save dialog.
#[tauri::command]
async fn download_host_logs(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    key: String,
) -> Result<Option<String>, ClientError> {
    use tauri_plugin_dialog::DialogExt;

    let (host, paired) = state.paired_host(&key)?;
    let (file_name, zip) = state.api.download_host_logs(&host, &paired).await?;
    let dialog = app.clone();
    let chosen = tauri::async_runtime::spawn_blocking(move || {
        dialog
            .dialog()
            .file()
            .set_title("Save the host logs")
            .set_file_name(&file_name)
            .add_filter("Zip files", &["zip"])
            .blocking_save_file()
    })
    .await
    .map_err(|e| ClientError::InvalidRequest(e.to_string()))?;

    let Some(path) = chosen.and_then(|file| file.into_path().ok()) else {
        return Ok(None);
    };
    std::fs::write(&path, zip).map_err(|e| ClientError::Storage(e.to_string()))?;
    Ok(Some(path.to_string_lossy().into_owned()))
}

/// This device's monitors, in the order mstsc numbers them.
#[tauri::command]
fn list_monitors() -> Vec<Monitor> {
    monitors::list()
}

/// The monitors Connect uses for a VM. Kept on this device, per paired host and VM.
#[tauri::command]
fn get_monitor_choice(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<MonitorChoice, ClientError> {
    let (_, paired) = state.paired_host(&key)?;
    Ok(state.monitors.get(&paired.host_id, &vm_id))
}

#[tauri::command]
fn set_monitor_choice(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    choice: MonitorChoice,
) -> Result<(), ClientError> {
    let (_, paired) = state.paired_host(&key)?;
    state.monitors.set(&paired.host_id, &vm_id, choice)
}

/// Opens the VM's console (its video output, also before an OS is installed) in mstsc, through
/// tunnels to the host. Works for any running VM; it does not need the VM to be set up.
#[tauri::command]
async fn open_console(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    let session = state.sessions.begin(&key);
    console::open(state.api.clone(), host, paired, vm_id, session).await
}

#[tauri::command]
async fn get_elevation(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.elevation_status(&host, &paired).await
}

/// Exchanges the host's admin passphrase for an elevation token, which stays in the Rust side.
#[tauri::command]
async fn elevate(
    state: State<'_, AppState>,
    key: String,
    mut passphrase: String,
) -> Result<api::ElevationGranted, ClientError> {
    let result = match state.paired_host(&key) {
        Ok((host, paired)) => state.api.elevate(&host, &paired, &passphrase).await,
        Err(error) => Err(error),
    };
    zeroize::Zeroize::zeroize(&mut passphrase);
    result
}

#[tauri::command]
async fn drop_elevation(state: State<'_, AppState>, key: String) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.drop_elevation(&host, &paired).await
}

#[tauri::command]
async fn perform_vm_action(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    action: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .perform_vm_action(&host, &paired, &vm_id, &action)
        .await
}

#[tauri::command]
async fn get_delete_preview(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.delete_preview(&host, &paired, &vm_id).await
}

#[tauri::command]
async fn delete_vm(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    request: api::DeleteVmRequest,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.delete_vm(&host, &paired, &vm_id, &request).await
}

#[tauri::command]
async fn create_vm(
    state: State<'_, AppState>,
    key: String,
    request: api::CreateVmRequest,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.create_vm(&host, &paired, &request).await
}

#[tauri::command]
async fn get_job(
    state: State<'_, AppState>,
    key: String,
    job_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.get_job(&host, &paired, &job_id).await
}

/// Host resources, the ISO library, or the virtual switches.
#[tauri::command]
async fn get_host_resource(
    state: State<'_, AppState>,
    key: String,
    resource: api::HostResource,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.get_resource(&host, &paired, resource).await
}

#[tauri::command]
async fn check_host_update(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.check_host_update(&host, &paired).await
}

/// Installs the host's ready update now (needs elevation). Returns the HostUpdateStatus.
#[tauri::command]
async fn install_host_update(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.install_host_update(&host, &paired).await
}

#[tauri::command]
async fn set_host_update_settings(
    state: State<'_, AppState>,
    key: String,
    settings: api::HostUpdateSettings,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .set_host_update_settings(&host, &paired, &settings)
        .await
}

/// The VM's unattended install (UnattendedInstallStatus), with the setup profile's result.
#[tauri::command]
async fn get_vm_install(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.get_vm_install(&host, &paired, &vm_id).await
}

#[tauri::command]
async fn get_vm_compute(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.get_vm_compute(&host, &paired, &vm_id).await
}

#[tauri::command]
async fn update_vm_compute(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    request: api::UpdateComputeRequest,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .update_vm_compute(&host, &paired, &vm_id, &request)
        .await
}

/// Event with the progress of an ISO upload: `{ pickId, sent, total }`.
const ISO_UPLOAD_PROGRESS_EVENT: &str = "iso-upload-progress";

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct PickedIso {
    /// Refers to the chosen file in `upload_iso`; the path itself stays on this side.
    pick_id: String,
    file_name: String,
    size_bytes: u64,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct UploadProgress {
    pick_id: String,
    sent: u64,
    total: u64,
}

/// Shows a file picker for an ISO image on this device. Returns None when the user cancels.
#[tauri::command]
async fn pick_iso_file(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> Result<Option<PickedIso>, ClientError> {
    use tauri_plugin_dialog::DialogExt;

    let dialog = app.clone();
    let picked = tauri::async_runtime::spawn_blocking(move || {
        dialog
            .dialog()
            .file()
            .set_title("Choose an ISO image to add to the host's library")
            .add_filter("ISO images", &["iso"])
            .blocking_pick_file()
    })
    .await
    .map_err(|e| ClientError::InvalidRequest(e.to_string()))?;

    let Some(path) = picked.and_then(|file| file.into_path().ok()) else {
        return Ok(None);
    };
    let size_bytes = std::fs::metadata(&path)
        .map_err(|e| ClientError::InvalidRequest(format!("the file could not be read: {e}")))?
        .len();
    let file_name = path
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default();

    let mut id = [0u8; 16];
    getrandom::fill(&mut id).map_err(|e| ClientError::InvalidRequest(e.to_string()))?;
    let pick_id = hex::encode(id);
    state
        .picked_isos
        .lock()
        .unwrap()
        .insert(pick_id.clone(), path);
    Ok(Some(PickedIso {
        pick_id,
        file_name,
        size_bytes,
    }))
}

/// Uploads a picked file to the host's ISO library as `name`, emitting progress events.
#[tauri::command]
async fn upload_iso(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    key: String,
    pick_id: String,
    name: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    let path = state
        .picked_isos
        .lock()
        .unwrap()
        .get(&pick_id)
        .cloned()
        .ok_or_else(|| ClientError::InvalidRequest("choose the file again".into()))?;
    let total = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
    let cancel = Arc::new(std::sync::atomic::AtomicBool::new(false));
    state
        .uploads
        .lock()
        .unwrap()
        .insert(pick_id.clone(), cancel.clone());

    // At most about two hundred events per upload: one per half percent.
    let last_reported = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let step = (total / 200).max(1);
    let events = app.clone();
    let id = pick_id.clone();
    let progress: Arc<dyn Fn(u64) + Send + Sync> = Arc::new(move |sent| {
        let last = last_reported.load(std::sync::atomic::Ordering::Relaxed);
        if sent == total || sent >= last + step {
            last_reported.store(sent, std::sync::atomic::Ordering::Relaxed);
            let _ = events.emit(
                ISO_UPLOAD_PROGRESS_EVENT,
                UploadProgress {
                    pick_id: id.clone(),
                    sent,
                    total,
                },
            );
        }
    });

    let result = state
        .api
        .upload_iso(&host, &paired, &name, &path, progress, cancel)
        .await;
    state.uploads.lock().unwrap().remove(&pick_id);
    if result.is_ok() {
        state.picked_isos.lock().unwrap().remove(&pick_id);
    }
    result
}

#[tauri::command]
fn cancel_iso_upload(state: State<'_, AppState>, pick_id: String) {
    if let Some(cancel) = state.uploads.lock().unwrap().get(&pick_id) {
        cancel.store(true, std::sync::atomic::Ordering::SeqCst);
    }
}

#[tauri::command]
async fn rename_iso(
    state: State<'_, AppState>,
    key: String,
    name: String,
    new_name: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.rename_iso(&host, &paired, &name, &new_name).await
}

#[tauri::command]
async fn delete_iso(
    state: State<'_, AppState>,
    key: String,
    name: String,
) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.delete_iso(&host, &paired, &name).await
}

/// A saved setup profile (StoredSetupProfile).
#[tauri::command]
async fn get_setup_profile(
    state: State<'_, AppState>,
    key: String,
    profile_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .get_setup_profile(&host, &paired, &profile_id)
        .await
}

/// Saves a new setup profile (no ID) or replaces one. Needs elevation.
#[tauri::command]
async fn save_setup_profile(
    state: State<'_, AppState>,
    key: String,
    profile_id: Option<String>,
    profile: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .save_setup_profile(&host, &paired, profile_id.as_deref(), &profile)
        .await
}

/// Deletes a setup profile. Needs elevation.
#[tauri::command]
async fn delete_setup_profile(
    state: State<'_, AppState>,
    key: String,
    profile_id: String,
) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .delete_setup_profile(&host, &paired, &profile_id)
        .await
}

/// The largest setup profile file the host accepts.
const MAX_PROFILE_FILE_BYTES: u64 = 256 * 1024;

/// Saves a setup profile's YAML file where the user chooses. Returns the path, or None when cancelled.
#[tauri::command]
async fn export_setup_profile(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    key: String,
    profile_id: String,
) -> Result<Option<String>, ClientError> {
    use tauri_plugin_dialog::DialogExt;

    let (host, paired) = state.paired_host(&key)?;
    let yaml = state
        .api
        .export_setup_profile(&host, &paired, &profile_id)
        .await?;
    let dialog = app.clone();
    let file_name = format!("{profile_id}.yaml");
    let chosen = tauri::async_runtime::spawn_blocking(move || {
        dialog
            .dialog()
            .file()
            .set_title("Save the setup profile")
            .set_file_name(&file_name)
            .add_filter("Setup profiles", &["yaml", "yml"])
            .blocking_save_file()
    })
    .await
    .map_err(|e| ClientError::InvalidRequest(e.to_string()))?;

    let Some(path) = chosen.and_then(|file| file.into_path().ok()) else {
        return Ok(None);
    };
    std::fs::write(&path, yaml).map_err(|e| ClientError::Storage(e.to_string()))?;
    Ok(Some(path.to_string_lossy().into_owned()))
}

/// Picks a YAML file on this device and saves it on the host as a new setup profile (needs elevation).
/// Returns None when the user cancels.
#[tauri::command]
async fn import_setup_profile(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    key: String,
) -> Result<Option<serde_json::Value>, ClientError> {
    use tauri_plugin_dialog::DialogExt;

    let (host, paired) = state.paired_host(&key)?;
    let dialog = app.clone();
    let picked = tauri::async_runtime::spawn_blocking(move || {
        dialog
            .dialog()
            .file()
            .set_title("Choose a setup profile to import")
            .add_filter("Setup profiles", &["yaml", "yml"])
            .blocking_pick_file()
    })
    .await
    .map_err(|e| ClientError::InvalidRequest(e.to_string()))?;

    let Some(path) = picked.and_then(|file| file.into_path().ok()) else {
        return Ok(None);
    };
    let size = std::fs::metadata(&path)
        .map_err(|e| ClientError::Storage(e.to_string()))?
        .len();
    if size > MAX_PROFILE_FILE_BYTES {
        return Err(ClientError::InvalidRequest(
            "a setup profile file is at most 256 KB".into(),
        ));
    }
    let yaml = std::fs::read_to_string(&path).map_err(|e| ClientError::Storage(e.to_string()))?;
    state
        .api
        .import_setup_profile(&host, &paired, &yaml)
        .await
        .map(Some)
}

/// winget packages found by the host.
#[tauri::command]
async fn search_packages(
    state: State<'_, AppState>,
    key: String,
    query: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.search_packages(&host, &paired, &query).await
}

/// An extension from a pasted store link or id.
#[tauri::command]
async fn resolve_extension(
    state: State<'_, AppState>,
    key: String,
    input: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.resolve_extension(&host, &paired, &input).await
}

/// A running Windows VM's provisioned Appx packages, compared with the clean baseline.
#[tauri::command]
async fn list_vm_appx(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.list_vm_appx(&host, &paired, &vm_id).await
}

/// Records a VM's provisioned packages as the clean baseline for its build and edition.
#[tauri::command]
async fn record_appx_baseline(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.record_appx_baseline(&host, &paired, &vm_id).await
}

/// A draft setup profile read from a running Windows VM.
#[tauri::command]
async fn capture_setup_profile(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .capture_setup_profile(&host, &paired, &vm_id)
        .await
}

/// What an ISO in the library installs, for the create dialog's unattended options.
#[tauri::command]
async fn inspect_iso(
    state: State<'_, AppState>,
    key: String,
    name: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.inspect_iso(&host, &paired, &name).await
}

/// Creates an unattended profile (no ID) or replaces one. Needs elevation.
#[tauri::command]
async fn save_unattend_profile(
    state: State<'_, AppState>,
    key: String,
    profile_id: Option<String>,
    profile: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .save_unattend_profile(&host, &paired, profile_id.as_deref(), &profile)
        .await
}

#[tauri::command]
async fn delete_unattend_profile(
    state: State<'_, AppState>,
    key: String,
    profile_id: String,
) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .delete_unattend_profile(&host, &paired, &profile_id)
        .await
}

#[tauri::command]
async fn get_vm_performance(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.get_vm_performance(&host, &paired, &vm_id).await
}

/// Applies a setup profile to a running Windows VM. Needs elevation; returns the job.
#[tauri::command]
async fn apply_vm_setup_profile(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    request: api::ApplySetupProfileRequest,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .apply_vm_setup_profile(&host, &paired, &vm_id, &request)
        .await
}

/// Turns Performance mode on or changes it. Needs elevation; returns the job.
#[tauri::command]
async fn apply_vm_performance(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    settings: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .apply_vm_performance(&host, &paired, &vm_id, &settings)
        .await
}

#[tauri::command]
async fn remove_vm_performance(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .remove_vm_performance(&host, &paired, &vm_id)
        .await
}

/// Copies the host's GPU driver into the guest (and, unless drivers only, writes the RDP settings).
#[tauri::command]
async fn set_up_performance_guest(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    drivers_only: bool,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .set_up_performance_guest(&host, &paired, &vm_id, drivers_only)
        .await
}

#[tauri::command]
async fn get_game_aware(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.get_game_aware(&host, &paired).await
}

/// Changes game-aware mode. Needs elevation.
#[tauri::command]
async fn set_game_aware(
    state: State<'_, AppState>,
    key: String,
    settings: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.set_game_aware(&host, &paired, &settings).await
}

#[tauri::command]
async fn not_a_game(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.not_a_game(&host, &paired).await
}

#[tauri::command]
async fn list_backup_targets(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.list_backup_targets(&host, &paired).await
}

/// Adds a backup target. Needs elevation. The request may hold remote storage keys.
#[tauri::command]
async fn create_backup_target(
    state: State<'_, AppState>,
    key: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .create_backup_target(&host, &paired, &request)
        .await
}

/// Adds a target that already holds backups, using its saved recovery key. Needs elevation.
#[tauri::command]
async fn attach_backup_target(
    state: State<'_, AppState>,
    key: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .attach_backup_target(&host, &paired, &request)
        .await
}

#[tauri::command]
async fn update_backup_target(
    state: State<'_, AppState>,
    key: String,
    target_id: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .update_backup_target(&host, &paired, &target_id, &request)
        .await
}

#[tauri::command]
async fn delete_backup_target(
    state: State<'_, AppState>,
    key: String,
    target_id: String,
) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .delete_backup_target(&host, &paired, &target_id)
        .await
}

#[tauri::command]
async fn test_backup_target(
    state: State<'_, AppState>,
    key: String,
    target_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .test_backup_target(&host, &paired, &target_id)
        .await
}

/// Generates a target's recovery key. Needs elevation. The key is shown once and not stored by the client.
#[tauri::command]
async fn create_recovery_key(
    state: State<'_, AppState>,
    key: String,
    target_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .create_recovery_key(&host, &paired, &target_id)
        .await
}

#[tauri::command]
async fn confirm_recovery_key(
    state: State<'_, AppState>,
    key: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .confirm_recovery_key(&host, &paired, &request)
        .await
}

#[tauri::command]
async fn list_backup_policies(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.list_backup_policies(&host, &paired).await
}

#[tauri::command]
async fn set_backup_policy(
    state: State<'_, AppState>,
    key: String,
    policy: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.set_backup_policy(&host, &paired, &policy).await
}

#[tauri::command]
async fn delete_backup_policy(
    state: State<'_, AppState>,
    key: String,
    target_id: String,
    vm_id: String,
) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .delete_backup_policy(&host, &paired, &target_id, &vm_id)
        .await
}

#[tauri::command]
async fn get_backup_status(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.get_backup_status(&host, &paired).await
}

#[tauri::command]
async fn list_backup_history(
    state: State<'_, AppState>,
    key: String,
    limit: u32,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.list_backup_history(&host, &paired, limit).await
}

/// Backs up now. Returns the job.
#[tauri::command]
async fn start_backup_run(
    state: State<'_, AppState>,
    key: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.start_backup_run(&host, &paired, &request).await
}

#[tauri::command]
async fn get_restore_test(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.get_restore_test(&host, &paired).await
}

#[tauri::command]
async fn set_restore_test_settings(
    state: State<'_, AppState>,
    key: String,
    settings: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .set_restore_test_settings(&host, &paired, &settings)
        .await
}

/// Runs the restore test now. It continues on the host; poll the status.
#[tauri::command]
async fn start_restore_test(
    state: State<'_, AppState>,
    key: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.start_restore_test(&host, &paired).await
}

/// Opens a snapshot for browsing. Needs elevation.
#[tauri::command]
async fn open_backup_browse(
    state: State<'_, AppState>,
    key: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.open_backup_browse(&host, &paired, &request).await
}

#[tauri::command]
async fn get_backup_browse(
    state: State<'_, AppState>,
    key: String,
    session_id: String,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .get_backup_browse(&host, &paired, &session_id)
        .await
}

#[tauri::command]
async fn close_backup_browse(
    state: State<'_, AppState>,
    key: String,
    session_id: String,
) -> Result<(), ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .close_backup_browse(&host, &paired, &session_id)
        .await
}

/// Lists a folder inside an open backup. Needs elevation.
#[tauri::command]
async fn list_backup_browse(
    state: State<'_, AppState>,
    key: String,
    session_id: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .list_backup_browse(&host, &paired, &session_id, &request)
        .await
}

/// Saves one file from an open backup where the user chooses. Needs elevation. Returns the path, or None when the
/// user cancels the save dialog.
#[tauri::command]
async fn download_backup_browse_file(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    key: String,
    session_id: String,
    volume: String,
    path: String,
) -> Result<Option<String>, ClientError> {
    use tauri_plugin_dialog::DialogExt;

    let (host, paired) = state.paired_host(&key)?;
    let leaf = path
        .rsplit(['\\', '/'])
        .find(|part| !part.is_empty())
        .unwrap_or("file")
        .to_string();
    let dialog = app.clone();
    let chosen = tauri::async_runtime::spawn_blocking(move || {
        dialog
            .dialog()
            .file()
            .set_title("Save the file from the backup")
            .set_file_name(&leaf)
            .blocking_save_file()
    })
    .await
    .map_err(|e| ClientError::InvalidRequest(e.to_string()))?;
    let Some(destination) = chosen.and_then(|file| file.into_path().ok()) else {
        return Ok(None);
    };
    let body = serde_json::json!({ "volume": volume, "path": path });
    state
        .api
        .download_backup_browse_file(&host, &paired, &session_id, &body, &destination)
        .await?;
    Ok(Some(destination.to_string_lossy().into_owned()))
}

/// Lists what a target holds, grouped by VM. Needs elevation.
#[tauri::command]
async fn scan_backups(
    state: State<'_, AppState>,
    key: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.scan_backups(&host, &paired, &request).await
}

/// Restores a snapshot as a new VM. Needs elevation; returns the job.
#[tauri::command]
async fn restore_vm_backup(
    state: State<'_, AppState>,
    key: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.restore_vm_backup(&host, &paired, &request).await
}

/// Restores the host's own settings from a snapshot. Needs elevation; returns the job.
#[tauri::command]
async fn restore_host_state(
    state: State<'_, AppState>,
    key: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state.api.restore_host_state(&host, &paired, &request).await
}

/// Puts a snapshot's disks over a VM's disks. Needs elevation; returns the job.
#[tauri::command]
async fn replace_vm_disk(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    request: serde_json::Value,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .replace_vm_disk(&host, &paired, &vm_id, &request)
        .await
}

#[tauri::command]
async fn export_vm_disks(
    state: State<'_, AppState>,
    key: String,
    vm_id: String,
    destination_folder: Option<String>,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .export_vm_disks(&host, &paired, &vm_id, destination_folder.as_deref())
        .await
}

/// Sends Wake-on-LAN magic packets using the cached adapter details. Returns datagrams sent.
/// Wakes a host. On the host's own network this PC sends the magic packet. Away from it, any other paired host that is
/// online on that network is asked to send it (the best candidate first), so a host can be woken from anywhere.
#[tauri::command]
async fn wake_host(state: State<'_, AppState>, key: String) -> Result<usize, ClientError> {
    let (_, paired) = state.paired_host(&key)?;
    if paired.wake_adapters.is_empty() || wake::on_same_lan(&paired.wake_adapters) {
        return wake::wake(&paired.wake_adapters);
    }

    let hosts = state.all_hosts();
    let all_paired = state.paired.all();
    let candidates: Vec<tailnet::RelayCandidate<'_>> = all_paired
        .iter()
        .filter_map(|candidate| {
            hosts
                .iter()
                .find(|entry| {
                    entry
                        .host_id
                        .as_deref()
                        .is_some_and(|id| id.eq_ignore_ascii_case(&candidate.host_id))
                        || candidate.entry_keys.contains(&entry.key)
                })
                .map(|entry| tailnet::RelayCandidate {
                    paired: candidate,
                    entry,
                })
        })
        .collect();

    for relay in tailnet::rank_wake_relays(&paired, &candidates) {
        let Some(entry) = candidates
            .iter()
            .find(|candidate| candidate.paired.host_id == relay.host_id)
            .map(|candidate| candidate.entry)
        else {
            continue;
        };
        if let Ok(sent) = state
            .api
            .relay_wake(entry, relay, &paired.wake_adapters)
            .await
        {
            return Ok(sent);
        }
    }
    Err(ClientError::NoWakeRelay)
}

/// Tailscale on this PC, read now.
#[tauri::command]
async fn get_tailnet_status(
    state: State<'_, AppState>,
) -> Result<tailnet::TailnetStatus, ClientError> {
    let port = state.tailnet.settings.get().local_api_port;
    Ok(state
        .tailnet
        .read_now(&tailnet::HttpSource { port }, &tailnet::CliSource)
        .await)
}

/// Looks at the tailnet again and tells the list to reload when it found something new.
#[tauri::command]
async fn refresh_tailnet(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> Result<(), ClientError> {
    if state.refresh_tailnet().await {
        let _ = app.emit(HOSTS_CHANGED_EVENT, ());
    }
    Ok(())
}

#[tauri::command]
fn get_tailnet_settings(state: State<'_, AppState>) -> tailnet::TailnetSettings {
    state.tailnet.settings.get()
}

#[tauri::command]
fn set_tailnet_settings(
    state: State<'_, AppState>,
    settings: tailnet::TailnetSettings,
) -> Result<(), ClientError> {
    state.tailnet.settings.set(settings)
}

/// How the last request to a host reached it: over the LAN or over Tailscale (Direct or Relayed).
#[tauri::command]
fn get_connection_route(
    state: State<'_, AppState>,
    key: String,
) -> Result<tailnet::ConnectionRoute, ClientError> {
    let host = state.host(&key)?;
    let last_good = state
        .pairing_for(&host)
        .and_then(|paired| state.api.last_good_url(&paired.host_id));
    Ok(tailnet::describe_route(&host, last_good.as_deref()))
}

/// Checks the way to a host over Tailscale, one step at a time, and says where it stops.
#[tauri::command]
async fn test_tailscale_reachability(
    state: State<'_, AppState>,
    key: String,
) -> Result<Vec<tailnet::ReachabilityStep>, ClientError> {
    let host = state.host(&key)?;
    let paired = state.pairing_for(&host);
    let port = state.tailnet.settings.get().local_api_port;

    let address: Option<String> = host
        .tailscale
        .as_ref()
        .and_then(|info| tailnet::first_ipv4(&info.addresses))
        .or_else(|| {
            host.addresses
                .iter()
                .find(|address| {
                    tailnet::is_tailnet_ip(address) && address.parse::<std::net::Ipv4Addr>().is_ok()
                })
                .map(String::as_str)
        })
        .map(str::to_string);

    // Ping first, so Tailscale has chosen a path (direct or relayed) when the status is read.
    if let Some(address) = &address {
        tailnet::warm_path(address).await;
    }
    let status = state
        .tailnet
        .read_now(&tailnet::HttpSource { port }, &tailnet::CliSource)
        .await;

    let mut certificate = tailnet::CertCheck::NoAnswer;
    let mut api = tailnet::ApiCheck::NotTried;
    if let (Some(address), true) = (&address, status.running()) {
        if let Some(hash) = state.api.probe_certificate(address, host.port).await {
            certificate = match &paired {
                None => tailnet::CertCheck::Unpaired,
                Some(paired) if paired.certificate_hash() == Some(hash) => {
                    tailnet::CertCheck::Pinned
                }
                Some(_) => tailnet::CertCheck::Different,
            };
            if let (tailnet::CertCheck::Pinned, Some(paired)) = (&certificate, &paired) {
                api = state
                    .api
                    .check_via_address(paired, address, host.port)
                    .await;
            }
        }
    }

    Ok(tailnet::build_reachability(&tailnet::ReachabilityInput {
        status: &status,
        address: address.as_deref(),
        port: host.port,
        certificate,
        api,
    }))
}

/// Changes where the host finds its Tailscale client and the control server in the command it suggests (needs
/// elevation). Returns the host's HostTailscale.
#[tauri::command]
async fn set_host_tailscale_settings(
    state: State<'_, AppState>,
    key: String,
    local_api_port: u16,
    control_url: Option<String>,
) -> Result<serde_json::Value, ClientError> {
    let (host, paired) = state.paired_host(&key)?;
    state
        .api
        .set_host_tailscale_settings(&host, &paired, local_api_port, control_url.as_deref())
        .await
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
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
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
                api: Arc::new(ApiClient::new(identity, local_host_name)),
                pending: tokio::sync::Mutex::new(HashMap::new()),
                picked_isos: std::sync::Mutex::new(HashMap::new()),
                uploads: std::sync::Mutex::new(HashMap::new()),
                monitors: MonitorChoiceStore::new(Some(config_dir.join("client-settings.json"))),
                sessions: sessions::ActiveSessions::default(),
                camera: camera::service::CameraService::new(
                    Arc::new(camera::backend::WindowsBackend),
                    Some(config_dir.join("camera-settings.json")),
                    config_dir.join("camera-ledger.json"),
                ),
                tailnet: Arc::new(tailnet::TailnetService::new(
                    tailnet::TailnetSettingsStore::new(Some(config_dir.join("tailscale.json"))),
                )),
                updates: {
                    let version = app.package_info().version.to_string();
                    let handle = app.handle().clone();
                    update::service::UpdateService::new(
                        Arc::new(update::plugin::PluginBackend::new(app.handle().clone())),
                        update::settings::UpdateSettingsStore::new(
                            Some(config_dir.join("update-settings.json")),
                            &version,
                        ),
                        &version,
                        update::install_kind::detect(),
                        Arc::new(move |status| {
                            let _ = handle.emit(CLIENT_UPDATE_EVENT, status);
                        }),
                    )
                },
                _mdns: mdns,
            });

            // Look at the tailnet every half minute. A PC without Tailscale answers "not installed" quickly and
            // costs nothing more.
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                let mut ticks = tokio::time::interval(TAILNET_REFRESH_INTERVAL);
                ticks.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
                loop {
                    ticks.tick().await;
                    let state = handle.state::<AppState>();
                    if state.refresh_tailnet().await {
                        let _ = handle.emit(HOSTS_CHANGED_EVENT, ());
                    }
                }
            });

            // Look for a new version of this program shortly after start and then once a day, unless
            // the user turned that off. Installing always waits for the user.
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                tokio::time::sleep(UPDATE_FIRST_CHECK_DELAY).await;
                let mut ticks = tokio::time::interval(UPDATE_CHECK_INTERVAL);
                loop {
                    ticks.tick().await;
                    let state = handle.state::<AppState>();
                    if state.updates.settings().check_automatically {
                        state.updates.check().await;
                    }
                }
            });

            // While a Remote Desktop or console window is open, tell its host it is still in use, so a
            // host woken by Wake-on-LAN does not go back to sleep under the session.
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                let mut ticks = tokio::time::interval(sessions::CHECK_IN_INTERVAL);
                loop {
                    ticks.tick().await;
                    let state = handle.state::<AppState>();
                    for key in state.sessions.keys() {
                        if let Ok((host, paired)) = state.paired_host(&key) {
                            let _ = state.api.ping(&host, &paired).await;
                        }
                    }
                }
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
            get_tailnet_status,
            refresh_tailnet,
            get_tailnet_settings,
            set_tailnet_settings,
            get_connection_route,
            test_tailscale_reachability,
            set_host_tailscale_settings,
            get_wake_readiness,
            fix_wake,
            start_wake_test,
            provision_vm,
            get_elevation,
            elevate,
            drop_elevation,
            perform_vm_action,
            get_client_update,
            check_client_update,
            install_client_update,
            get_update_settings,
            set_update_settings,
            get_camera_status,
            camera_source_outdated,
            setup_camera_sharing,
            remove_camera_sharing,
            get_camera_prefs,
            set_camera_prefs,
            set_camera_shutter,
            get_camera_shutter,
            get_delete_preview,
            delete_vm,
            create_vm,
            get_job,
            get_host_resource,
            check_host_update,
            install_host_update,
            set_host_update_settings,
            get_vm_install,
            apply_vm_setup_profile,
            get_vm_compute,
            update_vm_compute,
            pick_iso_file,
            upload_iso,
            cancel_iso_upload,
            rename_iso,
            delete_iso,
            connect_vm,
            connect_host,
            enable_host_remote_desktop,
            download_host_logs,
            open_console,
            list_monitors,
            get_monitor_choice,
            set_monitor_choice,
            inspect_iso,
            get_setup_profile,
            save_setup_profile,
            delete_setup_profile,
            export_setup_profile,
            import_setup_profile,
            search_packages,
            resolve_extension,
            list_vm_appx,
            record_appx_baseline,
            capture_setup_profile,
            save_unattend_profile,
            delete_unattend_profile,
            get_vm_performance,
            apply_vm_performance,
            remove_vm_performance,
            set_up_performance_guest,
            get_game_aware,
            set_game_aware,
            not_a_game,
            list_backup_targets,
            create_backup_target,
            attach_backup_target,
            update_backup_target,
            delete_backup_target,
            test_backup_target,
            create_recovery_key,
            confirm_recovery_key,
            list_backup_policies,
            set_backup_policy,
            delete_backup_policy,
            get_backup_status,
            open_backup_browse,
            get_backup_browse,
            close_backup_browse,
            list_backup_browse,
            download_backup_browse_file,
            get_restore_test,
            set_restore_test_settings,
            start_restore_test,
            list_backup_history,
            start_backup_run,
            scan_backups,
            restore_vm_backup,
            restore_host_state,
            replace_vm_disk,
            export_vm_disks
        ])
        .build(tauri::generate_context!())
        .expect("error while building HyperHarbor client")
        .run(|app, event| {
            // Release the physical camera and remove the virtual cameras before the process ends.
            if let tauri::RunEvent::Exit = event {
                app.state::<AppState>().camera.shutdown();
            }
        });
}
