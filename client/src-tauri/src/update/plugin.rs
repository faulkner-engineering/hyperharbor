//! The real `Backend`: tauri-plugin-updater. The plugin checks the minisign signature of the
//! download against the public key in tauri.conf.json before it runs the installer.

use super::service::{AvailableUpdate, Backend, BoxFuture};
use std::sync::Mutex;
use tauri::AppHandle;
use tauri_plugin_updater::{Update, UpdaterExt};

pub struct PluginBackend {
    app: AppHandle,
    /// The update the last check found, for `install`.
    found: Mutex<Option<Update>>,
}

impl PluginBackend {
    pub fn new(app: AppHandle) -> Self {
        Self {
            app,
            found: Mutex::new(None),
        }
    }
}

impl Backend for PluginBackend {
    fn check<'a>(
        &'a self,
        endpoint: &'a str,
    ) -> BoxFuture<'a, Result<Option<AvailableUpdate>, String>> {
        Box::pin(async move {
            let url = endpoint.parse().map_err(|e| format!("{e}"))?;
            let updater = self
                .app
                .updater_builder()
                .endpoints(vec![url])
                .map_err(|e| e.to_string())?
                .build()
                .map_err(|e| e.to_string())?;
            let update = updater.check().await.map_err(|e| e.to_string())?;
            let available = update.as_ref().map(|update| AvailableUpdate {
                version: update.version.clone(),
                notes: update.body.clone().filter(|notes| !notes.trim().is_empty()),
            });
            *self.found.lock().unwrap() = update;
            Ok(available)
        })
    }

    fn install<'a>(
        &'a self,
        progress: &'a (dyn Fn(u64, Option<u64>) + Send + Sync),
    ) -> BoxFuture<'a, Result<(), String>> {
        Box::pin(async move {
            let update = self.found.lock().unwrap().take().ok_or_else(|| {
                "There is no update to install. Check for updates first.".to_string()
            })?;
            let mut downloaded = 0u64;
            update
                .download_and_install(
                    |chunk, total| {
                        downloaded += chunk as u64;
                        progress(downloaded, total);
                    },
                    || {},
                )
                .await
                .map_err(|e| e.to_string())
        })
    }
}
