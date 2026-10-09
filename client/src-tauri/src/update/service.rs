//! The client's own update: checks the channel's manifest, downloads and installs a new version
//! when the user agrees.
//!
//! The updater plugin is reached through `Backend`, so the state machine is tested without the
//! network. An installed copy replaces itself; a portable copy only reports that a version exists.

use super::install_kind::InstallKind;
use super::settings::{UpdateChannel, UpdateSettings, UpdateSettingsStore};
use crate::error::ClientError;
use serde::Serialize;
use std::future::Future;
use std::pin::Pin;
use std::sync::{Arc, Mutex};

pub type BoxFuture<'a, T> = Pin<Box<dyn Future<Output = T> + Send + 'a>>;

/// Where people download a build by hand (portable copies, and installs that cannot update).
pub const RELEASES_URL: &str = "https://github.com/faulkner-engineering/hyperharbor/releases";

const STABLE_ENDPOINT: &str =
    "https://github.com/faulkner-engineering/hyperharbor/releases/latest/download/client-latest.json";
const BETA_ENDPOINT: &str =
    "https://github.com/faulkner-engineering/hyperharbor/releases/download/channel-beta/client-latest.json";

/// The manifest of a channel. Both are fixed https addresses on the project's GitHub releases.
pub fn endpoint(channel: UpdateChannel) -> &'static str {
    match channel {
        UpdateChannel::Stable => STABLE_ENDPOINT,
        UpdateChannel::Beta => BETA_ENDPOINT,
    }
}

/// A version newer than the running one.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AvailableUpdate {
    pub version: String,
    pub notes: Option<String>,
}

/// The updater plugin, as the service needs it.
pub trait Backend: Send + Sync {
    /// Reads the manifest at `endpoint`; `None` when this version is the newest. The result is kept
    /// for `install`.
    fn check<'a>(
        &'a self,
        endpoint: &'a str,
    ) -> BoxFuture<'a, Result<Option<AvailableUpdate>, String>>;
    /// Downloads the update found by the last `check`, verifies its signature, and starts the
    /// installer. `progress` receives the bytes downloaded so far and the total, when known.
    fn install<'a>(
        &'a self,
        progress: &'a (dyn Fn(u64, Option<u64>) + Send + Sync),
    ) -> BoxFuture<'a, Result<(), String>>;
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(tag = "state", rename_all = "camelCase")]
pub enum UpdatePhase {
    Idle,
    Checking,
    UpToDate,
    Available(AvailableUpdate),
    #[serde(rename_all = "camelCase")]
    Downloading {
        downloaded: u64,
        total: Option<u64>,
    },
    Failed {
        message: String,
    },
}

/// What the page shows.
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateStatus {
    pub current_version: String,
    pub install_kind: InstallKind,
    pub releases_url: &'static str,
    pub phase: UpdatePhase,
}

type Notify = Arc<dyn Fn(&UpdateStatus) + Send + Sync>;

pub struct UpdateService {
    backend: Arc<dyn Backend>,
    settings: UpdateSettingsStore,
    current_version: String,
    kind: InstallKind,
    phase: Mutex<UpdatePhase>,
    notify: Notify,
}

impl UpdateService {
    pub fn new(
        backend: Arc<dyn Backend>,
        settings: UpdateSettingsStore,
        current_version: &str,
        kind: InstallKind,
        notify: Notify,
    ) -> Self {
        Self {
            backend,
            settings,
            current_version: current_version.to_string(),
            kind,
            phase: Mutex::new(UpdatePhase::Idle),
            notify,
        }
    }

    pub fn settings(&self) -> UpdateSettings {
        self.settings.get()
    }

    /// Saves the preferences. A different channel forgets what an earlier check found, because the
    /// other channel may not offer it.
    pub fn set_settings(&self, settings: UpdateSettings) -> Result<UpdateStatus, ClientError> {
        let channel_changed = self.settings.get().channel != settings.channel;
        self.settings.set(settings)?;
        if channel_changed && !self.busy() {
            self.set_phase(UpdatePhase::Idle);
        }
        Ok(self.status())
    }

    pub fn status(&self) -> UpdateStatus {
        UpdateStatus {
            current_version: self.current_version.clone(),
            install_kind: self.kind,
            releases_url: RELEASES_URL,
            phase: self.phase.lock().unwrap().clone(),
        }
    }

    fn busy(&self) -> bool {
        matches!(
            *self.phase.lock().unwrap(),
            UpdatePhase::Checking | UpdatePhase::Downloading { .. }
        )
    }

    fn set_phase(&self, phase: UpdatePhase) {
        *self.phase.lock().unwrap() = phase;
        (self.notify)(&self.status());
    }

    /// Looks for a newer version on the chosen channel. A failure is part of the status, not an
    /// error: a manual check shows it and the automatic one stays quiet.
    pub async fn check(&self) -> UpdateStatus {
        {
            let mut phase = self.phase.lock().unwrap();
            if matches!(
                *phase,
                UpdatePhase::Checking | UpdatePhase::Downloading { .. }
            ) {
                drop(phase);
                return self.status();
            }
            *phase = UpdatePhase::Checking;
        }
        (self.notify)(&self.status());

        let found = self
            .backend
            .check(endpoint(self.settings.get().channel))
            .await;
        self.set_phase(match found {
            Ok(Some(update)) => UpdatePhase::Available(update),
            Ok(None) => UpdatePhase::UpToDate,
            Err(message) => UpdatePhase::Failed { message },
        });
        self.status()
    }

    /// Downloads and installs the version the last check found. The installer closes the app and
    /// starts the new version. `sessions` is the number of open Remote Desktop and console windows;
    /// closing the app would end them, so the user must confirm.
    pub async fn install(&self, sessions: usize, confirmed: bool) -> Result<(), ClientError> {
        if self.kind == InstallKind::Portable {
            return Err(ClientError::UpdateFailed(
                "A portable copy cannot update itself. Download the new version from the releases page."
                    .into(),
            ));
        }
        {
            let mut phase = self.phase.lock().unwrap();
            if !matches!(*phase, UpdatePhase::Available(_)) {
                return Err(ClientError::UpdateFailed(
                    "There is no update to install. Check for updates first.".into(),
                ));
            }
            if sessions > 0 && !confirmed {
                return Err(ClientError::SessionsActive(sessions));
            }
            *phase = UpdatePhase::Downloading {
                downloaded: 0,
                total: None,
            };
        }
        (self.notify)(&self.status());

        let report = |downloaded: u64, total: Option<u64>| {
            self.set_phase(UpdatePhase::Downloading { downloaded, total });
        };
        match self.backend.install(&report).await {
            Ok(()) => Ok(()),
            Err(message) => {
                self.set_phase(UpdatePhase::Failed {
                    message: message.clone(),
                });
                Err(ClientError::UpdateFailed(message))
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    #[derive(Default)]
    struct FakeBackend {
        found: Mutex<Option<Result<Option<AvailableUpdate>, String>>>,
        install_result: Mutex<Option<Result<(), String>>>,
        endpoints: Mutex<Vec<String>>,
        installs: AtomicUsize,
    }

    impl Backend for FakeBackend {
        fn check<'a>(
            &'a self,
            endpoint: &'a str,
        ) -> BoxFuture<'a, Result<Option<AvailableUpdate>, String>> {
            self.endpoints.lock().unwrap().push(endpoint.to_string());
            let result = self.found.lock().unwrap().clone().unwrap_or(Ok(None));
            Box::pin(async move { result })
        }

        fn install<'a>(
            &'a self,
            progress: &'a (dyn Fn(u64, Option<u64>) + Send + Sync),
        ) -> BoxFuture<'a, Result<(), String>> {
            self.installs.fetch_add(1, Ordering::SeqCst);
            progress(50, Some(100));
            let result = self
                .install_result
                .lock()
                .unwrap()
                .clone()
                .unwrap_or(Ok(()));
            Box::pin(async move { result })
        }
    }

    fn newer() -> AvailableUpdate {
        AvailableUpdate {
            version: "0.2.0".into(),
            notes: Some("Notes".into()),
        }
    }

    fn service(
        backend: &Arc<FakeBackend>,
        kind: InstallKind,
        version: &str,
    ) -> (UpdateService, Arc<Mutex<Vec<UpdatePhase>>>) {
        let seen = Arc::new(Mutex::new(Vec::new()));
        let sink = seen.clone();
        let service = UpdateService::new(
            backend.clone(),
            UpdateSettingsStore::new(None, version),
            version,
            kind,
            Arc::new(move |status| sink.lock().unwrap().push(status.phase.clone())),
        );
        (service, seen)
    }

    #[tokio::test]
    async fn a_check_reports_a_newer_version() {
        let backend = Arc::new(FakeBackend::default());
        *backend.found.lock().unwrap() = Some(Ok(Some(newer())));
        let (service, seen) = service(&backend, InstallKind::Installed, "0.1.7");

        let status = service.check().await;

        assert_eq!(status.phase, UpdatePhase::Available(newer()));
        assert_eq!(
            *seen.lock().unwrap(),
            vec![UpdatePhase::Checking, UpdatePhase::Available(newer())]
        );
    }

    #[tokio::test]
    async fn a_check_with_nothing_newer_is_up_to_date() {
        let backend = Arc::new(FakeBackend::default());
        let (service, _) = service(&backend, InstallKind::Installed, "0.1.7");
        assert_eq!(service.check().await.phase, UpdatePhase::UpToDate);
    }

    #[tokio::test]
    async fn a_failed_check_is_part_of_the_status() {
        let backend = Arc::new(FakeBackend::default());
        *backend.found.lock().unwrap() = Some(Err("offline".into()));
        let (service, _) = service(&backend, InstallKind::Installed, "0.1.7");
        assert_eq!(
            service.check().await.phase,
            UpdatePhase::Failed {
                message: "offline".into()
            }
        );
    }

    #[tokio::test]
    async fn the_channel_picks_the_manifest() {
        let backend = Arc::new(FakeBackend::default());
        let (stable, _) = service(&backend, InstallKind::Installed, "0.1.7");
        let (beta, _) = service(&backend, InstallKind::Installed, "0.1.7-beta.1");
        stable.check().await;
        beta.check().await;
        assert_eq!(
            *backend.endpoints.lock().unwrap(),
            vec![STABLE_ENDPOINT.to_string(), BETA_ENDPOINT.to_string()]
        );
    }

    #[test]
    fn both_manifests_are_https_on_the_project_releases() {
        for channel in [UpdateChannel::Stable, UpdateChannel::Beta] {
            assert!(endpoint(channel).starts_with(RELEASES_URL.replace("/releases", "").as_str()));
            assert!(endpoint(channel).starts_with("https://"));
        }
    }

    #[tokio::test]
    async fn install_needs_a_found_update() {
        let backend = Arc::new(FakeBackend::default());
        let (service, _) = service(&backend, InstallKind::Installed, "0.1.7");
        let error = service.install(0, false).await.unwrap_err();
        assert!(matches!(error, ClientError::UpdateFailed(_)));
        assert_eq!(backend.installs.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn install_reports_progress() {
        let backend = Arc::new(FakeBackend::default());
        *backend.found.lock().unwrap() = Some(Ok(Some(newer())));
        let (service, seen) = service(&backend, InstallKind::Installed, "0.1.7");
        service.check().await;

        service.install(0, false).await.unwrap();

        assert!(seen.lock().unwrap().contains(&UpdatePhase::Downloading {
            downloaded: 50,
            total: Some(100)
        }));
        assert_eq!(backend.installs.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn open_sessions_need_the_users_confirmation() {
        let backend = Arc::new(FakeBackend::default());
        *backend.found.lock().unwrap() = Some(Ok(Some(newer())));
        let (service, _) = service(&backend, InstallKind::Installed, "0.1.7");
        service.check().await;

        let error = service.install(2, false).await.unwrap_err();
        assert!(matches!(error, ClientError::SessionsActive(2)));
        assert_eq!(backend.installs.load(Ordering::SeqCst), 0);
        assert_eq!(service.status().phase, UpdatePhase::Available(newer()));

        service.install(2, true).await.unwrap();
        assert_eq!(backend.installs.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn a_portable_copy_never_installs() {
        let backend = Arc::new(FakeBackend::default());
        *backend.found.lock().unwrap() = Some(Ok(Some(newer())));
        let (service, _) = service(&backend, InstallKind::Portable, "0.1.7");
        service.check().await;

        assert!(service.install(0, true).await.is_err());
        assert_eq!(backend.installs.load(Ordering::SeqCst), 0);
        assert_eq!(service.status().phase, UpdatePhase::Available(newer()));
    }

    #[tokio::test]
    async fn a_failed_install_is_reported_and_can_be_retried() {
        let backend = Arc::new(FakeBackend::default());
        *backend.found.lock().unwrap() = Some(Ok(Some(newer())));
        *backend.install_result.lock().unwrap() = Some(Err("bad signature".into()));
        let (service, _) = service(&backend, InstallKind::Installed, "0.1.7");
        service.check().await;

        let error = service.install(0, false).await.unwrap_err();

        assert!(matches!(error, ClientError::UpdateFailed(ref m) if m == "bad signature"));
        assert_eq!(
            service.status().phase,
            UpdatePhase::Failed {
                message: "bad signature".into()
            }
        );
        // A new check finds the update again, so the user can try once more.
        assert_eq!(service.check().await.phase, UpdatePhase::Available(newer()));
    }

    #[tokio::test]
    async fn changing_the_channel_forgets_the_found_update() {
        let backend = Arc::new(FakeBackend::default());
        *backend.found.lock().unwrap() = Some(Ok(Some(newer())));
        let (service, _) = service(&backend, InstallKind::Installed, "0.1.7");
        service.check().await;

        let status = service
            .set_settings(UpdateSettings {
                channel: UpdateChannel::Beta,
                check_automatically: true,
            })
            .unwrap();

        assert_eq!(status.phase, UpdatePhase::Idle);
    }

    #[test]
    fn the_status_uses_the_names_the_page_reads() {
        let status = UpdateStatus {
            current_version: "0.1.7".into(),
            install_kind: InstallKind::Portable,
            releases_url: RELEASES_URL,
            phase: UpdatePhase::Downloading {
                downloaded: 1,
                total: None,
            },
        };
        let json = serde_json::to_value(&status).unwrap();
        assert_eq!(json["installKind"], "portable");
        assert_eq!(json["phase"]["state"], "downloading");
        assert_eq!(json["phase"]["downloaded"], 1);
        let available = serde_json::to_value(UpdatePhase::Available(newer())).unwrap();
        assert_eq!(available["state"], "available");
        assert_eq!(available["version"], "0.2.0");
    }
}
