//! The camera service: owns the physical camera, one virtual camera per Remote Desktop session,
//! and the policy for each.
//!
//! A session goes through three calls. `prepare` creates its virtual camera and returns what the
//! .rdp file should redirect; `attach_process` tells the service which mstsc process shows the
//! session (for focus); `end` removes the camera. The physical camera is opened when the first
//! session needs it and released after the last.
//!
//! The operating system is reached through `Backend`, so the logic is tested without cameras.

use super::capture::CaptureEvent;
use super::failure::CameraFailure;
use super::fanout::{Fanout, Sink};
use super::frame::FrameSize;
use super::ledger::{Ledger, LedgerEntry};
use super::naming;
use super::policy::{FrameMode, SessionPolicy, UnfocusedBehavior};
use super::reconcile::ReconcileReport;
use super::resolve::{wait_for_symbolic_link, DeviceEnumerator, VideoDevice};
use super::support::Support;
use super::{CameraError, CameraRedirect};
use hyperharbor_vcam_protocol::Status;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{channel, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

/// How long to wait for a new virtual camera to be listed.
const DEVICE_WAIT: Duration = Duration::from_secs(5);
const DEVICE_POLL: Duration = Duration::from_millis(100);
/// Frames older than this mean the camera is not delivering (busy, unplugged, or starting).
const FRAME_STALE: Duration = Duration::from_millis(1_500);

/// The fan-out key of the camera for this PC's own applications.
const LOCAL_KEY: &str = "local:this-pc";
/// Its name is "HyperHarbor Camera (This PC)".
const LOCAL_LABEL: &str = "This PC";

/// Anything the service holds only to release it when dropped (a virtual camera, a capture thread).
pub type Held = Box<dyn Send>;

/// The operating system, as the service needs it.
pub trait Backend: Send + Sync {
    fn support(&self) -> Support;
    /// Creates a virtual camera named `name`; dropping the result removes it.
    fn create_camera(&self, name: &str) -> Result<Held, CameraError>;
    /// Serves the frame pipe for the camera named `name`.
    fn start_pipe(&self, name: &str) -> Result<Box<dyn Sink>, CameraError>;
    fn devices(&self) -> Result<Vec<VideoDevice>, CameraError>;
    /// Starts capturing the physical camera; dropping the result stops it.
    fn start_capture(&self, events: Sender<CaptureEvent>) -> Held;
    /// The process owning the foreground window, if any.
    fn foreground_pid(&self) -> Option<u32>;
    fn own_pid(&self) -> u32;
    /// Whether the camera source DLL is registered, so virtual cameras can be created.
    fn source_installed(&self) -> bool;
    /// Removes virtual cameras left by a client that did not shut down cleanly.
    fn reconcile(&self, ledger: &mut Ledger) -> Result<ReconcileReport, CameraError>;
}

struct BackendDevices<'a>(&'a dyn Backend);

impl DeviceEnumerator for BackendDevices<'_> {
    fn video_devices(&self) -> Result<Vec<VideoDevice>, CameraError> {
        self.0.devices()
    }
}

/// What a VM's camera does; kept per paired host and VM on this device.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CameraPrefs {
    /// Redirect the camera to this VM at all.
    #[serde(default = "yes")]
    pub share: bool,
    /// What the VM sees while its window is not in front.
    #[serde(default)]
    pub unfocused: UnfocusedBehavior,
}

fn yes() -> bool {
    true
}

impl Default for CameraPrefs {
    fn default() -> Self {
        Self {
            share: true,
            unfocused: UnfocusedBehavior::default(),
        }
    }
}

/// The saved preferences, in a JSON file on this device.
pub struct PrefsStore {
    path: Option<PathBuf>,
    prefs: Mutex<BTreeMap<String, CameraPrefs>>,
}

#[derive(Default, Serialize, Deserialize)]
struct PrefsFile {
    #[serde(default)]
    vms: BTreeMap<String, CameraPrefs>,
}

impl PrefsStore {
    pub fn new(path: Option<PathBuf>) -> Self {
        let prefs = path
            .as_ref()
            .and_then(|path| std::fs::read(path).ok())
            .and_then(|bytes| serde_json::from_slice::<PrefsFile>(&bytes).ok())
            .map(|file| file.vms)
            .unwrap_or_default();
        Self {
            path,
            prefs: Mutex::new(prefs),
        }
    }

    pub fn get(&self, vm_key: &str) -> CameraPrefs {
        self.prefs
            .lock()
            .ok()
            .and_then(|prefs| prefs.get(vm_key).copied())
            .unwrap_or_default()
    }

    pub fn set(&self, vm_key: &str, value: CameraPrefs) -> Result<(), CameraError> {
        let mut prefs = self
            .prefs
            .lock()
            .map_err(|_| CameraError::Ledger("settings are locked".into()))?;
        if value == CameraPrefs::default() {
            prefs.remove(vm_key);
        } else {
            prefs.insert(vm_key.to_string(), value);
        }
        let Some(path) = &self.path else {
            return Ok(());
        };
        let json = serde_json::to_vec_pretty(&PrefsFile { vms: prefs.clone() })
            .map_err(|e| CameraError::Ledger(e.to_string()))?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| CameraError::Ledger(e.to_string()))?;
        }
        let temporary = path.with_extension("json.tmp");
        std::fs::write(&temporary, json).map_err(|e| CameraError::Ledger(e.to_string()))?;
        std::fs::rename(&temporary, path).map_err(|e| CameraError::Ledger(e.to_string()))
    }
}

/// What `prepare` returns.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Prepared {
    pub redirect: CameraRedirect,
    /// Something the user should know: sharing is unavailable, or the camera goes to one VM only.
    pub notice: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionStatus {
    pub vm_key: String,
    pub vm_name: String,
    /// True for a virtual camera of its own; false when the physical camera is redirected directly.
    pub shared: bool,
    /// What the VM sees now: "live", "frozen", "blurred", or "shuttered"; none when not shared.
    pub mode: Option<&'static str>,
    pub shutter: bool,
    pub focused: bool,
}

/// Shown when virtual cameras could be made but the camera source is not registered yet.
pub const NOT_SET_UP: &str = "Camera sharing is not set up on this device. Open Camera… on a VM and choose Set up camera sharing.";

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CameraStatus {
    /// True when this Windows version can create virtual cameras (Windows 11).
    pub sharing_available: bool,
    pub sharing_message: Option<String>,
    /// True when the camera source is registered. When it is not, "Set up camera sharing" installs it.
    pub source_installed: bool,
    /// "idle" (no sessions), "active", "busy" (another app has it), or "unavailable".
    pub camera: &'static str,
    pub camera_message: Option<String>,
    pub device: Option<String>,
    /// The name of the camera this PC's own applications can use while a VM is connected; None when
    /// none is. It always shows live video.
    pub local_camera: Option<String>,
    pub sessions: Vec<SessionStatus>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum Health {
    Idle,
    Active {
        device: String,
    },
    Failed {
        failure: CameraFailure,
        detail: String,
    },
}

struct Session {
    vm_name: String,
    /// The virtual camera's name; None when the physical camera is redirected directly.
    camera_name: Option<String>,
    /// Released when the session ends.
    _camera: Option<Held>,
    pid: Option<u32>,
    refs: u32,
}

/// The virtual camera for applications on this PC. It exists while at least one VM session has a
/// virtual camera of its own, and always shows the live picture: nothing changes it (no shutter,
/// no blur, no freezing), which is what a camera for the PC's own use should do.
struct LocalCamera {
    name: String,
    /// Released when the last VM camera goes, which removes the camera.
    _camera: Held,
}

struct State {
    local: Option<LocalCamera>,
    sessions: HashMap<String, Session>,
    fanout: Fanout,
    capture: Option<Held>,
    health: Health,
    last_frame_at: Option<Instant>,
    frame_size: FrameSize,
    /// Shutters outlive a session's window but not the app: closing it before reconnecting counts.
    shutters: HashMap<String, bool>,
    ledger: Ledger,
    /// The VM that has the physical camera redirected directly (no virtual cameras available).
    physical_holder: Option<String>,
}

pub struct CameraService {
    backend: Arc<dyn Backend>,
    state: Arc<Mutex<State>>,
    prefs: PrefsStore,
    events: Sender<CaptureEvent>,
    stop: Arc<AtomicBool>,
    worker: Mutex<Option<JoinHandle<()>>>,
    support: Support,
}

/// The names of the virtual cameras that exist, so a new one never repeats one.
fn taken_names(state: &State) -> Vec<String> {
    state
        .sessions
        .values()
        .filter_map(|session| session.camera_name.clone())
        .chain(state.local.as_ref().map(|local| local.name.clone()))
        .collect()
}

/// Explains why a virtual camera could not be created, in words for the user.
pub fn describe_create_error(error: &CameraError) -> String {
    let text = error.to_string();
    // REGDB_E_CLASSNOTREG: the source DLL is not registered (a portable copy, or a failed install).
    if text.contains("0x80040154") {
        NOT_SET_UP.to_string()
    } else {
        format!("Camera sharing could not start: {text}")
    }
}

impl CameraService {
    /// Starts the service and its worker thread. `ledger_path` and `prefs_path` may be None.
    pub fn new(
        backend: Arc<dyn Backend>,
        prefs_path: Option<PathBuf>,
        ledger_path: PathBuf,
    ) -> Arc<Self> {
        let (service, receiver) = Self::build(backend, prefs_path, ledger_path);
        let worker = {
            let service = service.clone();
            std::thread::spawn(move || service.work(&receiver))
        };
        if let Ok(mut slot) = service.worker.lock() {
            *slot = Some(worker);
        }
        service
    }

    /// The service without its worker thread, for tests that drive it with `process` and `tick`.
    pub fn build(
        backend: Arc<dyn Backend>,
        prefs_path: Option<PathBuf>,
        ledger_path: PathBuf,
    ) -> (Arc<Self>, Receiver<CaptureEvent>) {
        let (events, receiver) = channel();
        let support = backend.support();
        let service = Arc::new(Self {
            backend,
            state: Arc::new(Mutex::new(State {
                local: None,
                sessions: HashMap::new(),
                fanout: Fanout::new(),
                capture: None,
                health: Health::Idle,
                last_frame_at: None,
                frame_size: FrameSize::HD,
                shutters: HashMap::new(),
                ledger: Ledger::load(&ledger_path),
                physical_holder: None,
            })),
            prefs: PrefsStore::new(prefs_path),
            events,
            stop: Arc::new(AtomicBool::new(false)),
            worker: Mutex::new(None),
            support,
        });
        (service, receiver)
    }

    pub fn prefs(&self, vm_key: &str) -> CameraPrefs {
        self.prefs.get(vm_key)
    }

    /// Saves a VM's preferences; they apply to its running session at once.
    pub fn set_prefs(&self, vm_key: &str, value: CameraPrefs) -> Result<(), CameraError> {
        self.prefs.set(vm_key, value)?;
        if let Ok(mut state) = self.state.lock() {
            if let Some(policy) = state.fanout.policy_mut(vm_key) {
                policy.set_unfocused_behavior(value.unfocused);
            }
        }
        Ok(())
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, State> {
        self.state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Makes ready what a session needs and says what its .rdp file should redirect.
    pub fn prepare(&self, vm_key: &str, vm_name: &str) -> Prepared {
        let prefs = self.prefs.get(vm_key);
        if !prefs.share {
            return Prepared {
                redirect: CameraRedirect::None,
                notice: None,
            };
        }
        let mut state = self.lock();

        if let Some(existing) = state.sessions.get_mut(vm_key) {
            // Connecting again to a VM whose window is still open: the same camera serves it.
            existing.refs += 1;
            let redirect = match &existing.camera_name {
                Some(name) => self.redirect_for(name).unwrap_or(CameraRedirect::None),
                None => CameraRedirect::AllPhysical,
            };
            return Prepared {
                redirect,
                notice: None,
            };
        }

        if !self.support.is_available() {
            return self.prepare_physical(&mut state, vm_key, vm_name, self.support.message());
        }
        if !self.backend.source_installed() {
            return self.prepare_physical(
                &mut state,
                vm_key,
                vm_name,
                Some(NOT_SET_UP.to_string()),
            );
        }

        match self.create_virtual(&mut state, vm_key, vm_name, prefs) {
            Ok(redirect) => Prepared {
                redirect,
                notice: None,
            },
            Err(error) => {
                let notice = describe_create_error(&error);
                self.prepare_physical(&mut state, vm_key, vm_name, Some(notice))
            }
        }
    }

    /// The camera goes to this VM directly, as before camera sharing existed, if no other VM has
    /// it. A second VM gets no camera, because mstsc would give the physical camera to one session.
    fn prepare_physical(
        &self,
        state: &mut State,
        vm_key: &str,
        vm_name: &str,
        reason: Option<String>,
    ) -> Prepared {
        match &state.physical_holder {
            Some(holder) if holder != vm_key => Prepared {
                redirect: CameraRedirect::None,
                notice: Some(
                    reason
                        .map(|r| format!("{r} The camera is already in use by another VM."))
                        .unwrap_or_else(|| "The camera is already in use by another VM.".into()),
                ),
            },
            _ => {
                state.physical_holder = Some(vm_key.to_string());
                state.sessions.insert(
                    vm_key.to_string(),
                    Session {
                        vm_name: vm_name.to_string(),
                        camera_name: None,
                        _camera: None,
                        pid: None,
                        refs: 1,
                    },
                );
                Prepared {
                    redirect: CameraRedirect::AllPhysical,
                    notice: reason,
                }
            }
        }
    }

    fn create_virtual(
        &self,
        state: &mut State,
        vm_key: &str,
        vm_name: &str,
        prefs: CameraPrefs,
    ) -> Result<CameraRedirect, CameraError> {
        let name = naming::unique_friendly_name(vm_name, &taken_names(state));

        // The pipe exists before the camera, so the source finds it when the Frame Server starts it.
        let sink = self.backend.start_pipe(&name)?;
        let camera = self.backend.create_camera(&name)?;
        let redirect = self.redirect_for(&name)?;

        let mut policy = SessionPolicy::new(prefs.unfocused);
        policy.set_shutter(state.shutters.get(vm_key).copied().unwrap_or(false));
        state.fanout.add(vm_key, policy, sink);
        let _ = state.ledger.add(LedgerEntry {
            name: name.clone(),
            client_pid: self.backend.own_pid(),
            created_unix: std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs())
                .unwrap_or(0),
        });
        state.sessions.insert(
            vm_key.to_string(),
            Session {
                vm_name: vm_name.to_string(),
                camera_name: Some(name),
                _camera: Some(camera),
                pid: None,
                refs: 1,
            },
        );
        self.ensure_local(state);
        if state.capture.is_none() {
            state.capture = Some(self.backend.start_capture(self.events.clone()));
        }
        Ok(redirect)
    }

    /// Makes the camera for this PC's applications if it is not there. A failure is not reported:
    /// the VM sessions work without it.
    fn ensure_local(&self, state: &mut State) {
        if state.local.is_some() {
            return;
        }
        let name = naming::unique_friendly_name(LOCAL_LABEL, &taken_names(state));
        let Ok(sink) = self.backend.start_pipe(&name) else {
            return;
        };
        let Ok(camera) = self.backend.create_camera(&name) else {
            return;
        };
        // Never focused and never frozen: the policy that always shows live video.
        state.fanout.add(
            LOCAL_KEY,
            SessionPolicy::new(UnfocusedBehavior::KeepLive),
            sink,
        );
        let _ = state.ledger.add(LedgerEntry {
            name: name.clone(),
            client_pid: self.backend.own_pid(),
            created_unix: std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs())
                .unwrap_or(0),
        });
        state.local = Some(LocalCamera {
            name,
            _camera: camera,
        });
    }

    /// Removes the camera for this PC's applications.
    fn remove_local(&self, state: &mut State) {
        state.fanout.remove(LOCAL_KEY);
        if let Some(local) = state.local.take() {
            let name = local.name.clone();
            drop(local); // removes the virtual camera
            let _ = state.ledger.remove(&name);
        }
    }

    fn redirect_for(&self, name: &str) -> Result<CameraRedirect, CameraError> {
        let link = wait_for_symbolic_link(
            &BackendDevices(self.backend.as_ref()),
            name,
            DEVICE_WAIT,
            DEVICE_POLL,
        )?;
        CameraRedirect::link(link)
    }

    /// Records the mstsc process that shows a session, for focus tracking.
    pub fn attach_process(&self, vm_key: &str, pid: u32) {
        if let Some(session) = self.lock().sessions.get_mut(vm_key) {
            session.pid = Some(pid);
        }
    }

    /// Ends one use of a session; the camera is removed with the last one.
    pub fn end(&self, vm_key: &str) {
        let mut state = self.lock();
        let Some(session) = state.sessions.get_mut(vm_key) else {
            return;
        };
        session.refs = session.refs.saturating_sub(1);
        if session.refs > 0 {
            return;
        }
        let Some(session) = state.sessions.remove(vm_key) else {
            return;
        };
        state.fanout.remove(vm_key);
        let name = session.camera_name.clone();
        drop(session); // removes the virtual camera
        if let Some(name) = name {
            let _ = state.ledger.remove(&name);
        }
        if state.physical_holder.as_deref() == Some(vm_key) {
            state.physical_holder = None;
        }
        // The camera for this PC's applications is there only while a VM has a virtual camera.
        if !state.sessions.values().any(|s| s.camera_name.is_some()) {
            self.remove_local(&mut state);
        }
        if state.fanout.is_empty() {
            state.capture = None; // releases the physical camera
            state.health = Health::Idle;
            state.last_frame_at = None;
        }
    }

    /// Whether a VM's privacy shutter is closed.
    pub fn shutter_closed(&self, vm_key: &str) -> bool {
        self.lock().shutters.get(vm_key).copied().unwrap_or(false)
    }

    /// Closes or opens a VM's privacy shutter. It applies at once and to later sessions.
    pub fn set_shutter(&self, vm_key: &str, closed: bool) {
        let mut state = self.lock();
        state.shutters.insert(vm_key.to_string(), closed);
        if let Some(policy) = state.fanout.policy_mut(vm_key) {
            policy.set_shutter(closed);
        }
    }

    /// Handles one event from the capture thread.
    pub fn process(&self, event: CaptureEvent, now: Instant) {
        let mut state = self.lock();
        match event {
            CaptureEvent::Started { size, device } => {
                state.frame_size = size;
                state.health = Health::Active { device };
            }
            CaptureEvent::Frame(frame) => {
                state.last_frame_at = Some(now);
                state.frame_size = frame.size;
                if !matches!(state.health, Health::Active { .. }) {
                    state.health = Health::Active {
                        device: String::new(),
                    };
                }
                self.update_focus(&mut state, now);
                state.fanout.on_frame(&frame, now);
            }
            CaptureEvent::Failed { failure, detail } => {
                state.health = Health::Failed { failure, detail };
                state.last_frame_at = None;
            }
        }
    }

    /// Runs on a timer: focus, and keepalives while no frames arrive.
    pub fn tick(&self, now: Instant) {
        let mut state = self.lock();
        self.update_focus(&mut state, now);
        let stale = state
            .last_frame_at
            .is_none_or(|at| now.saturating_duration_since(at) >= FRAME_STALE);
        if stale && !state.fanout.is_empty() {
            let status = match &state.health {
                Health::Failed {
                    failure: CameraFailure::InUse,
                    ..
                } => Status::CameraBusy,
                _ => Status::Live,
            };
            let size = state.frame_size;
            state.fanout.on_idle(status, size, now);
        }
    }

    fn update_focus(&self, state: &mut State, now: Instant) {
        let foreground = self.backend.foreground_pid();
        let focused: Vec<(String, bool)> = state
            .sessions
            .iter()
            .filter(|(_, session)| session.camera_name.is_some())
            .map(|(key, session)| {
                (
                    key.clone(),
                    foreground.is_some() && session.pid == foreground,
                )
            })
            .collect();
        for (key, is_focused) in focused {
            if let Some(policy) = state.fanout.policy_mut(&key) {
                policy.set_focused(is_focused, now);
            }
        }
    }

    pub fn status(&self) -> CameraStatus {
        let state = self.lock();
        let now = Instant::now();
        let (camera, camera_message, device) = match &state.health {
            Health::Idle => ("idle", None, None),
            Health::Active { device } => {
                ("active", None, (!device.is_empty()).then(|| device.clone()))
            }
            Health::Failed { failure, .. } => (
                if *failure == CameraFailure::InUse {
                    "busy"
                } else {
                    "unavailable"
                },
                Some(failure.message().to_string()),
                None,
            ),
        };
        let mut sessions: Vec<SessionStatus> = state
            .sessions
            .iter()
            .map(|(key, session)| {
                let policy = state.fanout.policy(key);
                SessionStatus {
                    vm_key: key.clone(),
                    vm_name: session.vm_name.clone(),
                    shared: session.camera_name.is_some(),
                    mode: policy.map(|p| match p.mode(now) {
                        FrameMode::Live => "live",
                        FrameMode::Frozen => "frozen",
                        FrameMode::Blurred => "blurred",
                        FrameMode::Shuttered => "shuttered",
                    }),
                    shutter: state.shutters.get(key).copied().unwrap_or(false),
                    focused: policy.is_some_and(|p| p.focused()),
                }
            })
            .collect();
        sessions.sort_by(|a, b| a.vm_name.cmp(&b.vm_name));
        CameraStatus {
            sharing_available: self.support.is_available(),
            sharing_message: self.support.message(),
            source_installed: self.backend.source_installed(),
            camera,
            camera_message,
            device,
            local_camera: state.local.as_ref().map(|local| local.name.clone()),
            sessions,
        }
    }

    /// Removes cameras a previous run left behind (see `reconcile`). Runs once at start.
    pub fn reconcile_orphans(&self) -> Result<ReconcileReport, CameraError> {
        let mut state = self.lock();
        self.backend.reconcile(&mut state.ledger)
    }

    fn work(&self, receiver: &Receiver<CaptureEvent>) {
        if self.support.is_available() {
            let _ = self.reconcile_orphans();
        }
        while !self.stop.load(Ordering::SeqCst) {
            match receiver.recv_timeout(Duration::from_millis(50)) {
                Ok(event) => self.process(event, Instant::now()),
                Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {}
                Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => return,
            }
            self.tick(Instant::now());
        }
    }

    /// Stops the worker, removes every virtual camera, and releases the physical camera.
    pub fn shutdown(&self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(worker) = self.worker.lock().ok().and_then(|mut slot| slot.take()) {
            let _ = worker.join();
        }
        let keys: Vec<String> = self.lock().sessions.keys().cloned().collect();
        for key in keys {
            loop {
                let remaining = self.lock().sessions.get(&key).map(|s| s.refs);
                if remaining.is_none() {
                    break;
                }
                self.end(&key);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::super::frame::Frame;
    use super::*;
    use std::sync::atomic::AtomicUsize;

    const SIZE: FrameSize = FrameSize::new(16, 8);

    /// Shared record of what the fake backend was asked to do.
    #[derive(Default)]
    struct Log {
        created: Mutex<Vec<String>>,
        removed: Mutex<Vec<String>>,
        pipes: Mutex<Vec<String>>,
        captures_started: AtomicUsize,
        captures_stopped: AtomicUsize,
        reconciled: AtomicUsize,
        not_installed: std::sync::atomic::AtomicBool,
        foreground: Mutex<Option<u32>>,
        sent: Mutex<Vec<(String, Status)>>,
        /// The listing links are resolved from: a created camera is added and a dropped one removed.
        devices: Mutex<Vec<VideoDevice>>,
    }

    const LOCAL_NAME: &str = "HyperHarbor Camera (This PC)";

    impl Log {
        fn vm_created(&self) -> Vec<String> {
            self.created
                .lock()
                .unwrap()
                .iter()
                .filter(|n| *n != LOCAL_NAME)
                .cloned()
                .collect()
        }
        fn vm_removed(&self) -> Vec<String> {
            self.removed
                .lock()
                .unwrap()
                .iter()
                .filter(|n| *n != LOCAL_NAME)
                .cloned()
                .collect()
        }
        fn vm_pipes(&self) -> Vec<String> {
            self.pipes
                .lock()
                .unwrap()
                .iter()
                .filter(|n| *n != LOCAL_NAME)
                .cloned()
                .collect()
        }
        fn vm_sent(&self) -> Vec<(String, Status)> {
            self.sent
                .lock()
                .unwrap()
                .iter()
                .filter(|(n, _)| n != LOCAL_NAME)
                .cloned()
                .collect()
        }
        fn local_sent(&self) -> Vec<Status> {
            self.sent
                .lock()
                .unwrap()
                .iter()
                .filter(|(n, _)| n == LOCAL_NAME)
                .map(|(_, s)| *s)
                .collect()
        }
    }

    struct Tracked(Arc<Log>, String);
    impl Drop for Tracked {
        fn drop(&mut self) {
            self.0.removed.lock().unwrap().push(self.1.clone());
            self.0
                .devices
                .lock()
                .unwrap()
                .retain(|d| !d.name.starts_with(&self.1));
        }
    }
    struct CaptureHandle(Arc<Log>);
    impl Drop for CaptureHandle {
        fn drop(&mut self) {
            self.0.captures_stopped.fetch_add(1, Ordering::SeqCst);
        }
    }
    struct NamedSink(Arc<Log>, String);
    impl Sink for NamedSink {
        fn send(&mut self, status: Status, _frame: &Frame) {
            self.0.sent.lock().unwrap().push((self.1.clone(), status));
        }
    }

    struct Fake {
        log: Arc<Log>,
        support: Support,
        fail_create: Mutex<Option<CameraError>>,
        /// Fail every camera after this many have been made (usize::MAX: never).
        fail_after: std::sync::atomic::AtomicUsize,
    }

    impl Fake {
        fn new(support: Support) -> (Arc<Self>, Arc<Log>) {
            let log = Arc::new(Log::default());
            log.devices.lock().unwrap().push(VideoDevice {
                name: "HD Pro Webcam C920".into(),
                symbolic_link: r"\\?\usb#vid_046d#c920\global".into(),
            });
            (
                Arc::new(Self {
                    log: log.clone(),
                    support,
                    fail_create: Mutex::new(None),
                    fail_after: std::sync::atomic::AtomicUsize::new(usize::MAX),
                }),
                log,
            )
        }
    }

    impl Backend for Fake {
        fn support(&self) -> Support {
            self.support.clone()
        }
        fn create_camera(&self, name: &str) -> Result<Held, CameraError> {
            if let Some(error) = self.fail_create.lock().unwrap().take() {
                return Err(error);
            }
            if self.log.created.lock().unwrap().len() >= self.fail_after.load(Ordering::SeqCst) {
                return Err(CameraError::Enumeration(
                    "no more cameras (0x80004005)".into(),
                ));
            }
            self.log.created.lock().unwrap().push(name.to_string());
            let n = self.log.created.lock().unwrap().len();
            self.log.devices.lock().unwrap().push(VideoDevice {
                name: format!("{name} (Windows Virtual Camera)"),
                symbolic_link: format!(r"\\?\swd#vcamdevapi#{n}#{{e5323777}}\{{guid{n}}}"),
            });
            Ok(Box::new(Tracked(self.log.clone(), name.to_string())))
        }
        fn start_pipe(&self, name: &str) -> Result<Box<dyn Sink>, CameraError> {
            self.log.pipes.lock().unwrap().push(name.to_string());
            Ok(Box::new(NamedSink(self.log.clone(), name.to_string())))
        }
        fn devices(&self) -> Result<Vec<VideoDevice>, CameraError> {
            Ok(self.log.devices.lock().unwrap().clone())
        }
        fn start_capture(&self, _events: Sender<CaptureEvent>) -> Held {
            self.log.captures_started.fetch_add(1, Ordering::SeqCst);
            Box::new(CaptureHandle(self.log.clone()))
        }
        fn foreground_pid(&self) -> Option<u32> {
            *self.log.foreground.lock().unwrap()
        }
        fn own_pid(&self) -> u32 {
            4242
        }
        fn source_installed(&self) -> bool {
            !self.log.not_installed.load(Ordering::SeqCst)
        }
        fn reconcile(&self, _ledger: &mut Ledger) -> Result<ReconcileReport, CameraError> {
            self.log.reconciled.fetch_add(1, Ordering::SeqCst);
            Ok(ReconcileReport::default())
        }
    }

    fn service(support: Support, tag: &str) -> (Arc<CameraService>, Arc<Fake>, Arc<Log>) {
        let (backend, log) = Fake::new(support);
        let ledger = std::env::temp_dir()
            .join(format!("hh-camsvc-{tag}-{}", std::process::id()))
            .join("ledger.json");
        let _ = std::fs::remove_dir_all(ledger.parent().unwrap());
        let (service, _receiver) = CameraService::build(backend.clone(), None, ledger);
        (service, backend, log)
    }

    fn frame(sequence: u64) -> Frame {
        Frame::new(SIZE, vec![50; SIZE.nv12_len()], sequence).unwrap()
    }

    fn link_of(prepared: &Prepared) -> String {
        match &prepared.redirect {
            CameraRedirect::Link(link) => link.clone(),
            other => panic!("expected a link, got {other:?}"),
        }
    }

    #[test]
    fn two_vms_get_two_different_virtual_cameras_and_one_physical_capture() {
        let (service, _backend, log) = service(Support::Available, "two");
        let work = service.prepare("host/work", "Work");
        let games = service.prepare("host/games", "Games");
        assert_ne!(link_of(&work), link_of(&games));
        assert_eq!(
            log.vm_created(),
            vec!["HyperHarbor Camera (Work)", "HyperHarbor Camera (Games)"]
        );
        assert_eq!(
            log.captures_started.load(Ordering::SeqCst),
            1,
            "the physical camera is opened once"
        );
        // Neither redirects the physical camera.
        assert!(!link_of(&work).contains("usb#"));
    }

    #[test]
    fn the_resolved_link_is_the_one_of_the_camera_just_created() {
        let (service, _backend, log) = service(Support::Available, "link");
        let prepared = service.prepare("host/work", "Work");
        let listed = log.devices.lock().unwrap().clone();
        let ours = listed
            .iter()
            .find(|d| d.name.starts_with("HyperHarbor Camera (Work)"))
            .unwrap();
        assert_eq!(link_of(&prepared), ours.symbolic_link);
    }

    #[test]
    fn the_pipe_is_served_before_the_camera_is_created() {
        let (service, _backend, log) = service(Support::Available, "order");
        service.prepare("host/work", "Work");
        assert_eq!(log.vm_pipes(), vec!["HyperHarbor Camera (Work)"]);
        // Every camera's pipe was served before the camera itself, the local one included.
        assert_eq!(
            log.pipes.lock().unwrap().len(),
            log.created.lock().unwrap().len()
        );
    }

    #[test]
    fn two_vms_with_the_same_name_get_distinct_names() {
        let (service, _backend, log) = service(Support::Available, "dup");
        service.prepare("host-a/vm1", "Work");
        service.prepare("host-b/vm9", "Work");
        assert_eq!(
            log.vm_created(),
            vec!["HyperHarbor Camera (Work)", "HyperHarbor Camera (Work #2)"]
        );
    }

    #[test]
    fn ending_a_session_removes_its_camera_and_the_last_one_releases_the_physical_camera() {
        let (service, _backend, log) = service(Support::Available, "end");
        service.prepare("host/work", "Work");
        service.prepare("host/games", "Games");
        service.end("host/work");
        assert_eq!(log.vm_removed(), vec!["HyperHarbor Camera (Work)"]);
        assert!(
            service.status().local_camera.is_some(),
            "a VM still has a camera, so the local one stays"
        );
        assert_eq!(
            log.captures_stopped.load(Ordering::SeqCst),
            0,
            "another session still needs it"
        );
        service.end("host/games");
        assert_eq!(log.vm_removed().len(), 2);
        assert!(service.status().local_camera.is_none());
        assert_eq!(log.captures_stopped.load(Ordering::SeqCst), 1);
        assert_eq!(service.status().camera, "idle");
        assert!(service.status().sessions.is_empty());
    }

    #[test]
    fn connecting_again_to_an_open_session_reuses_its_camera_until_both_end() {
        let (service, _backend, log) = service(Support::Available, "again");
        let first = service.prepare("host/work", "Work");
        let second = service.prepare("host/work", "Work");
        assert_eq!(link_of(&first), link_of(&second));
        assert_eq!(log.vm_created().len(), 1);
        service.end("host/work");
        assert!(
            log.removed.lock().unwrap().is_empty(),
            "one window is still open"
        );
        service.end("host/work");
        assert_eq!(log.vm_removed().len(), 1);
    }

    #[test]
    fn a_camera_for_this_pc_exists_while_a_vm_has_a_virtual_camera_and_only_then() {
        let (service, _backend, log) = service(Support::Available, "local");
        assert!(
            service.status().local_camera.is_none(),
            "nothing connected yet"
        );
        service.prepare("host/work", "Work");
        assert_eq!(service.status().local_camera.as_deref(), Some(LOCAL_NAME));
        assert!(log
            .created
            .lock()
            .unwrap()
            .contains(&LOCAL_NAME.to_string()));
        service.end("host/work");
        assert!(service.status().local_camera.is_none());
        assert!(log
            .removed
            .lock()
            .unwrap()
            .contains(&LOCAL_NAME.to_string()));
    }

    #[test]
    fn two_vms_share_one_camera_for_this_pc_which_outlasts_the_first_of_them() {
        let (service, _backend, log) = service(Support::Available, "localtwo");
        service.prepare("host/work", "Work");
        service.prepare("host/games", "Games");
        let made = log
            .created
            .lock()
            .unwrap()
            .iter()
            .filter(|n| *n == LOCAL_NAME)
            .count();
        assert_eq!(made, 1, "one camera for this PC, however many VMs");
        service.end("host/work");
        assert_eq!(service.status().local_camera.as_deref(), Some(LOCAL_NAME));
        service.end("host/games");
        assert!(service.status().local_camera.is_none());
        let gone = log
            .removed
            .lock()
            .unwrap()
            .iter()
            .filter(|n| *n == LOCAL_NAME)
            .count();
        assert_eq!(gone, 1);
    }

    #[test]
    fn the_camera_for_this_pc_always_shows_live_video_whatever_the_vms_do() {
        let (service, _backend, log) = service(Support::Available, "locallive");
        service.prepare("host/work", "Work");
        service.prepare("host/games", "Games");
        // Work is unfocused and frozen, Games has its shutter closed.
        service.set_shutter("host/games", true);
        let t0 = Instant::now();
        for n in 0..10u64 {
            service.process(
                CaptureEvent::Frame(frame(n)),
                t0 + Duration::from_millis(n * 33),
            );
        }
        let local = log.local_sent();
        assert_eq!(local.len(), 10, "every captured frame");
        assert!(local.iter().all(|status| *status == Status::Live));
        // The VMs, in contrast, did not get every frame.
        assert!(log.vm_sent().len() < 20);
    }

    #[test]
    fn closing_a_vms_shutter_leaves_the_camera_for_this_pc_alone() {
        let (service, _backend, log) = service(Support::Available, "localshutter");
        service.prepare("host/work", "Work");
        service.set_shutter("host/work", true);
        service.process(CaptureEvent::Frame(frame(1)), Instant::now());
        assert_eq!(log.local_sent(), vec![Status::Live]);
        assert!(
            service
                .status()
                .sessions
                .iter()
                .all(|s| s.vm_name != "This PC"),
            "it is not listed as a VM session"
        );
    }

    #[test]
    fn no_camera_for_this_pc_is_made_when_no_vm_gets_a_virtual_camera() {
        // Windows 10, not set up, and sharing turned off for the VM: no virtual cameras at all.
        let (first, _backend, log) =
            service(Support::NeedsWindows11 { build: 19045 }, "localnone1");
        first.prepare("host/work", "Work");
        assert!(log.created.lock().unwrap().is_empty());
        assert!(first.status().local_camera.is_none());

        let (second, _backend, log) = service(Support::Available, "localnone2");
        log.not_installed.store(true, Ordering::SeqCst);
        second.prepare("host/work", "Work");
        assert!(log.created.lock().unwrap().is_empty());

        let (third, _backend, log) = service(Support::Available, "localnone3");
        third
            .set_prefs(
                "host/work",
                CameraPrefs {
                    share: false,
                    ..CameraPrefs::default()
                },
            )
            .unwrap();
        third.prepare("host/work", "Work");
        assert!(log.created.lock().unwrap().is_empty());
        assert!(third.status().local_camera.is_none());
    }

    #[test]
    fn a_vm_named_like_the_local_camera_does_not_clash_with_it() {
        let (service, _backend, log) = service(Support::Available, "localclash");
        service.prepare("host/pc", "This PC");
        service.prepare("host/other", "Other");
        let created = log.created.lock().unwrap().clone();
        let mut unique = created.clone();
        unique.sort();
        unique.dedup();
        assert_eq!(created.len(), unique.len(), "{created:?}");
        assert!(created.contains(&"HyperHarbor Camera (This PC)".to_string()));
        assert!(created.contains(&"HyperHarbor Camera (This PC #2)".to_string()));
    }

    #[test]
    fn a_failure_making_the_camera_for_this_pc_does_not_stop_the_vm_session() {
        let (backend, log) = Fake::new(Support::Available);
        // The first camera (the VM's) is made; the second (for this PC) fails.
        backend.fail_after.store(1, Ordering::SeqCst);
        let ledger = std::env::temp_dir()
            .join(format!("hh-camsvc-localfail-{}", std::process::id()))
            .join("ledger.json");
        let (service, _receiver) = CameraService::build(backend, None, ledger);
        let prepared = service.prepare("host/work", "Work");
        assert!(matches!(prepared.redirect, CameraRedirect::Link(_)));
        assert!(service.status().local_camera.is_none());
        assert_eq!(log.vm_created().len(), 1);
    }

    #[test]
    fn ending_an_unknown_session_changes_nothing() {
        let (service, _backend, log) = service(Support::Available, "unknown");
        service.end("host/none");
        assert!(log.removed.lock().unwrap().is_empty());
    }

    #[test]
    fn windows_10_gives_the_first_vm_the_physical_camera_and_the_second_none() {
        let (service, _backend, log) = service(Support::NeedsWindows11 { build: 19045 }, "win10");
        let first = service.prepare("host/work", "Work");
        assert_eq!(first.redirect, CameraRedirect::AllPhysical);
        assert!(first.notice.unwrap().contains("requires Windows 11"));
        let second = service.prepare("host/games", "Games");
        assert_eq!(second.redirect, CameraRedirect::None);
        assert!(second
            .notice
            .unwrap()
            .contains("already in use by another VM"));
        assert!(
            log.created.lock().unwrap().is_empty(),
            "no virtual camera on Windows 10"
        );
        // When the first VM leaves, the camera can go to another.
        service.end("host/work");
        assert_eq!(
            service.prepare("host/games", "Games").redirect,
            CameraRedirect::AllPhysical
        );
    }

    #[test]
    fn a_missing_source_registration_falls_back_to_the_physical_camera_with_an_explanation() {
        let (service, backend, _log) = service(Support::Available, "notreg");
        *backend.fail_create.lock().unwrap() = Some(CameraError::Enumeration(
            "Class not registered (0x80040154)".into(),
        ));
        let prepared = service.prepare("host/work", "Work");
        assert_eq!(prepared.redirect, CameraRedirect::AllPhysical);
        assert!(prepared
            .notice
            .unwrap()
            .contains("not set up on this device"));
    }

    #[test]
    fn without_the_camera_source_installed_no_virtual_camera_is_tried() {
        let (service, _backend, log) = service(Support::Available, "notinstalled");
        log.not_installed.store(true, Ordering::SeqCst);
        let prepared = service.prepare("host/work", "Work");
        assert_eq!(prepared.redirect, CameraRedirect::AllPhysical);
        assert!(prepared.notice.unwrap().contains("Set up camera sharing"));
        assert!(log.created.lock().unwrap().is_empty());
        assert!(log.pipes.lock().unwrap().is_empty());
        assert!(!service.status().source_installed);
    }

    #[test]
    fn the_status_says_the_source_is_installed_when_it_is() {
        let (service, _backend, _log) = service(Support::Available, "installed");
        assert!(service.status().source_installed);
    }

    #[test]
    fn turning_sharing_off_for_a_vm_redirects_no_camera() {
        let (service, _backend, log) = service(Support::Available, "off");
        service
            .set_prefs(
                "host/work",
                CameraPrefs {
                    share: false,
                    ..CameraPrefs::default()
                },
            )
            .unwrap();
        let prepared = service.prepare("host/work", "Work");
        assert_eq!(prepared.redirect, CameraRedirect::None);
        assert!(log.created.lock().unwrap().is_empty());
    }

    #[test]
    fn the_focused_vm_is_live_and_the_other_follows_its_policy() {
        let (service, _backend, log) = service(Support::Available, "focus");
        service.prepare("host/work", "Work");
        service.prepare("host/games", "Games");
        service.attach_process("host/work", 100);
        service.attach_process("host/games", 200);
        *log.foreground.lock().unwrap() = Some(100);
        let t0 = Instant::now();
        for n in 0..5 {
            service.process(
                CaptureEvent::Frame(frame(n)),
                t0 + Duration::from_millis(n * 33),
            );
        }
        let status = service.status();
        let work = status
            .sessions
            .iter()
            .find(|s| s.vm_name == "Work")
            .unwrap();
        let games = status
            .sessions
            .iter()
            .find(|s| s.vm_name == "Games")
            .unwrap();
        assert!(work.focused && !games.focused);
        let sent = log.sent.lock().unwrap();
        assert_eq!(sent.iter().filter(|(n, _)| n.contains("Work")).count(), 5);
        assert_eq!(
            sent.iter().filter(|(n, _)| n.contains("Games")).count(),
            1,
            "frozen after one frame"
        );
    }

    #[test]
    fn switching_the_foreground_window_moves_focus_after_the_grace_period() {
        let (service, _backend, log) = service(Support::Available, "switch");
        service.prepare("host/work", "Work");
        service.prepare("host/games", "Games");
        service.attach_process("host/work", 100);
        service.attach_process("host/games", 200);
        let t0 = Instant::now();
        *log.foreground.lock().unwrap() = Some(100);
        service.process(CaptureEvent::Frame(frame(1)), t0);
        *log.foreground.lock().unwrap() = Some(200);
        service.process(
            CaptureEvent::Frame(frame(2)),
            t0 + Duration::from_millis(500),
        );
        let status = service.status();
        assert!(
            status
                .sessions
                .iter()
                .find(|s| s.vm_name == "Games")
                .unwrap()
                .focused
        );
        assert!(
            !status
                .sessions
                .iter()
                .find(|s| s.vm_name == "Work")
                .unwrap()
                .focused
        );
    }

    #[test]
    fn the_shutter_applies_at_once_and_to_a_reconnect() {
        let (service, _backend, log) = service(Support::Available, "shutter");
        service.prepare("host/work", "Work");
        service.attach_process("host/work", 100);
        *log.foreground.lock().unwrap() = Some(100);
        let t0 = Instant::now();
        service.process(CaptureEvent::Frame(frame(1)), t0);
        service.set_shutter("host/work", true);
        service.process(
            CaptureEvent::Frame(frame(2)),
            t0 + Duration::from_millis(33),
        );
        assert_eq!(log.vm_sent().last().unwrap().1, Status::Shuttered);
        assert_eq!(service.status().sessions[0].mode, Some("shuttered"));
        // Close the window, reconnect: the shutter is still closed.
        service.end("host/work");
        service.prepare("host/work", "Work");
        assert_eq!(service.status().sessions[0].mode, Some("shuttered"));
        assert!(service.status().sessions[0].shutter);
    }

    #[test]
    fn a_busy_camera_is_reported_and_sessions_are_kept_alive() {
        let (service, _backend, log) = service(Support::Available, "busy");
        service.prepare("host/work", "Work");
        let t0 = Instant::now();
        service.process(CaptureEvent::Frame(frame(1)), t0);
        service.process(
            CaptureEvent::Failed {
                failure: CameraFailure::InUse,
                detail: "preempted".into(),
            },
            t0 + Duration::from_millis(100),
        );
        let status = service.status();
        assert_eq!(status.camera, "busy");
        assert!(status
            .camera_message
            .unwrap()
            .starts_with("Another app is using your camera"));
        let before = log.vm_sent().len();
        service.tick(t0 + Duration::from_secs(2));
        let sent = log.vm_sent();
        assert_eq!(sent.len(), before + 1);
        assert_eq!(sent.last().unwrap().1, Status::CameraBusy);
    }

    #[test]
    fn the_camera_recovers_when_frames_return() {
        let (service, _backend, _log) = service(Support::Available, "recover");
        service.prepare("host/work", "Work");
        let t0 = Instant::now();
        service.process(
            CaptureEvent::Failed {
                failure: CameraFailure::InUse,
                detail: String::new(),
            },
            t0,
        );
        assert_eq!(service.status().camera, "busy");
        service.process(CaptureEvent::Frame(frame(1)), t0 + Duration::from_secs(5));
        assert_eq!(service.status().camera, "active");
    }

    #[test]
    fn unfocused_preference_changes_apply_to_a_running_session() {
        let (service, _backend, _log) = service(Support::Available, "prefs");
        service.prepare("host/work", "Work");
        assert_eq!(service.status().sessions[0].mode, Some("frozen"));
        service
            .set_prefs(
                "host/work",
                CameraPrefs {
                    share: true,
                    unfocused: UnfocusedBehavior::Blur,
                },
            )
            .unwrap();
        assert_eq!(service.status().sessions[0].mode, Some("blurred"));
    }

    #[test]
    fn every_virtual_camera_is_in_the_ledger_until_it_ends() {
        let (service, _backend, _log) = service(Support::Available, "ledger");
        service.prepare("host/work", "Work");
        // The VM's camera and the one for this PC.
        assert_eq!(service.lock().ledger.entries().len(), 2);
        assert_eq!(service.lock().ledger.entries()[0].client_pid, 4242);
        service.end("host/work");
        assert!(service.lock().ledger.entries().is_empty());
    }

    #[test]
    fn shutdown_removes_every_camera() {
        let (service, _backend, log) = service(Support::Available, "shutdown");
        service.prepare("host/work", "Work");
        service.prepare("host/work", "Work");
        service.prepare("host/games", "Games");
        service.shutdown();
        assert_eq!(log.vm_removed().len(), 2);
        assert_eq!(
            log.removed.lock().unwrap().len(),
            3,
            "and the camera for this PC"
        );
        assert_eq!(log.captures_stopped.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn preferences_are_saved_and_default_values_are_not_stored() {
        let dir = std::env::temp_dir().join(format!("hh-camprefs-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let path = dir.join("camera-settings.json");
        let store = PrefsStore::new(Some(path.clone()));
        assert_eq!(store.get("h/v"), CameraPrefs::default());
        let changed = CameraPrefs {
            share: false,
            unfocused: UnfocusedBehavior::Blur,
        };
        store.set("h/v", changed).unwrap();
        assert_eq!(PrefsStore::new(Some(path.clone())).get("h/v"), changed);
        store.set("h/v", CameraPrefs::default()).unwrap();
        assert!(!std::fs::read_to_string(&path).unwrap().contains("h/v"));
    }

    #[test]
    fn the_worker_cleans_up_orphans_once_when_it_starts() {
        let (backend, log) = Fake::new(Support::Available);
        let ledger = std::env::temp_dir()
            .join(format!("hh-camsvc-worker-{}", std::process::id()))
            .join("ledger.json");
        let service = CameraService::new(backend, None, ledger);
        let started = Instant::now();
        while log.reconciled.load(Ordering::SeqCst) == 0
            && started.elapsed() < Duration::from_secs(5)
        {
            std::thread::sleep(Duration::from_millis(10));
        }
        service.shutdown();
        assert_eq!(log.reconciled.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn nothing_is_cleaned_up_where_virtual_cameras_do_not_exist() {
        let (backend, log) = Fake::new(Support::NeedsWindows11 { build: 19045 });
        let ledger = std::env::temp_dir()
            .join(format!("hh-camsvc-win10-{}", std::process::id()))
            .join("ledger.json");
        let service = CameraService::new(backend, None, ledger);
        std::thread::sleep(Duration::from_millis(200));
        service.shutdown();
        assert_eq!(log.reconciled.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn create_errors_read_well() {
        let not_registered = CameraError::Enumeration("Class not registered (0x80040154)".into());
        assert!(describe_create_error(&not_registered).contains("Set up camera sharing"));
        let other = CameraError::Enumeration("Something (0x80004005)".into());
        assert!(describe_create_error(&other).starts_with("Camera sharing could not start"));
    }
}
