use std::path::PathBuf;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::error::ClientError;

/// Which release stream the client follows. Stable reads the newest published release; beta also
/// reads prereleases.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum UpdateChannel {
    #[default]
    Stable,
    Beta,
}

/// The client's update preferences, in update-settings.json under the app's config directory. They
/// stay on this device.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateSettings {
    pub channel: UpdateChannel,
    /// Check for a new version when the app starts and once a day while it runs.
    pub check_automatically: bool,
}

impl UpdateSettings {
    /// A prerelease build follows the beta channel until the user chooses otherwise, so a beta
    /// tester is not offered a downgrade to the last stable release.
    pub fn defaults_for(version: &str) -> Self {
        Self {
            channel: if version.contains('-') {
                UpdateChannel::Beta
            } else {
                UpdateChannel::Stable
            },
            check_automatically: true,
        }
    }
}

/// Reads and writes update-settings.json. A missing or unreadable file gives the defaults for the
/// running version.
pub struct UpdateSettingsStore {
    path: Option<PathBuf>,
    current: Mutex<UpdateSettings>,
}

impl UpdateSettingsStore {
    pub fn new(path: Option<PathBuf>, running_version: &str) -> Self {
        let settings = path
            .as_ref()
            .and_then(|path| std::fs::read_to_string(path).ok())
            .and_then(|text| serde_json::from_str::<UpdateSettings>(&text).ok())
            .unwrap_or_else(|| UpdateSettings::defaults_for(running_version));
        Self {
            path,
            current: Mutex::new(settings),
        }
    }

    pub fn get(&self) -> UpdateSettings {
        self.current.lock().unwrap().clone()
    }

    pub fn set(&self, settings: UpdateSettings) -> Result<(), ClientError> {
        let mut current = self.current.lock().unwrap();
        if let Some(path) = &self.path {
            let text = serde_json::to_string_pretty(&settings)
                .map_err(|e| ClientError::Storage(e.to_string()))?;
            if let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent).map_err(|e| ClientError::Storage(e.to_string()))?;
            }
            std::fs::write(path, text).map_err(|e| ClientError::Storage(e.to_string()))?;
        }
        *current = settings;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_prerelease_build_defaults_to_the_beta_channel() {
        assert_eq!(
            UpdateSettings::defaults_for("0.1.7-beta.1").channel,
            UpdateChannel::Beta
        );
        assert_eq!(
            UpdateSettings::defaults_for("0.1.7").channel,
            UpdateChannel::Stable
        );
        assert!(UpdateSettings::defaults_for("0.1.7").check_automatically);
    }

    fn test_dir(name: &str) -> PathBuf {
        let dir =
            std::env::temp_dir().join(format!("hyperharbor-update-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn settings_survive_a_restart() {
        let dir = test_dir("restart");
        let path = dir.join("nested").join("update-settings.json");
        let store = UpdateSettingsStore::new(Some(path.clone()), "0.1.7");
        store
            .set(UpdateSettings {
                channel: UpdateChannel::Beta,
                check_automatically: false,
            })
            .unwrap();

        let reopened = UpdateSettingsStore::new(Some(path), "0.1.7");
        assert_eq!(reopened.get().channel, UpdateChannel::Beta);
        assert!(!reopened.get().check_automatically);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn an_unreadable_file_gives_the_defaults() {
        let dir = test_dir("unreadable");
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("update-settings.json");
        std::fs::write(&path, "not json").unwrap();
        let store = UpdateSettingsStore::new(Some(path), "0.1.7-beta.1");
        assert_eq!(store.get(), UpdateSettings::defaults_for("0.1.7-beta.1"));
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn the_file_uses_camel_case_names() {
        let text = serde_json::to_string(&UpdateSettings::defaults_for("0.1.7")).unwrap();
        assert_eq!(text, r#"{"channel":"stable","checkAutomatically":true}"#);
    }
}
