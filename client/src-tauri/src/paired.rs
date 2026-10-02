//! Hosts this client has paired with, persisted in the app config directory. Nothing here is
//! secret: the host certificate is public and the client key lives in the credential store.

use std::path::PathBuf;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::error::ClientError;
use crate::wake::WakeAdapter;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PairedHost {
    pub host_id: String,
    pub device_id: String,
    /// SHA-256 of the pinned host certificate DER, uppercase hex.
    pub host_certificate_fingerprint: String,
    pub display_name: String,
    /// Host list keys that refer to this host (a discovered entry and any manual entries).
    pub entry_keys: Vec<String>,
    /// Last known Wake-on-LAN adapters, kept so the host can be woken while it is unreachable.
    #[serde(default)]
    pub wake_adapters: Vec<WakeAdapter>,
    /// When wake_adapters was last refreshed, in seconds since the Unix epoch.
    #[serde(default)]
    pub wake_refreshed_at: Option<u64>,
}

impl PairedHost {
    pub fn certificate_hash(&self) -> Option<[u8; 32]> {
        hex::decode(&self.host_certificate_fingerprint)
            .ok()?
            .try_into()
            .ok()
    }
}

pub struct PairedHostStore {
    path: Option<PathBuf>,
    hosts: Mutex<Vec<PairedHost>>,
}

impl PairedHostStore {
    pub fn new(path: Option<PathBuf>) -> Self {
        let hosts = path
            .as_ref()
            .and_then(|path| std::fs::read_to_string(path).ok())
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default();
        Self {
            path,
            hosts: Mutex::new(hosts),
        }
    }

    /// Finds the pairing for a host list entry, by host ID when known and otherwise by entry key.
    pub fn find(&self, host_id: Option<&str>, entry_key: &str) -> Option<PairedHost> {
        self.hosts
            .lock()
            .unwrap()
            .iter()
            .find(|paired| {
                host_id.is_some_and(|id| id.eq_ignore_ascii_case(&paired.host_id))
                    || paired.entry_keys.iter().any(|key| key == entry_key)
            })
            .cloned()
    }

    /// Records a pairing, replacing any earlier pairing with the same host.
    pub fn save(&self, host: PairedHost) -> Result<(), ClientError> {
        {
            let mut hosts = self.hosts.lock().unwrap();
            hosts.retain(|existing| !existing.host_id.eq_ignore_ascii_case(&host.host_id));
            hosts.push(host);
        }
        self.persist()
    }

    /// Stores fresh Wake-on-LAN adapters for a paired host.
    pub fn update_wake(
        &self,
        host_id: &str,
        adapters: Vec<WakeAdapter>,
        now: u64,
    ) -> Result<(), ClientError> {
        {
            let mut hosts = self.hosts.lock().unwrap();
            let Some(host) = hosts
                .iter_mut()
                .find(|h| h.host_id.eq_ignore_ascii_case(host_id))
            else {
                return Ok(());
            };
            host.wake_adapters = adapters;
            host.wake_refreshed_at = Some(now);
        }
        self.persist()
    }

    pub fn remove(&self, host_id: &str) -> Result<(), ClientError> {
        self.hosts
            .lock()
            .unwrap()
            .retain(|existing| !existing.host_id.eq_ignore_ascii_case(host_id));
        self.persist()
    }

    fn persist(&self) -> Result<(), ClientError> {
        let Some(path) = &self.path else {
            return Ok(());
        };
        let text = serde_json::to_string_pretty(&*self.hosts.lock().unwrap())
            .map_err(|e| ClientError::Storage(e.to_string()))?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| ClientError::Storage(e.to_string()))?;
        }
        std::fs::write(path, text).map_err(|e| ClientError::Storage(e.to_string()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn host(id: &str, keys: &[&str]) -> PairedHost {
        PairedHost {
            host_id: id.into(),
            device_id: "device".into(),
            host_certificate_fingerprint: "AB".repeat(32),
            display_name: "PC".into(),
            entry_keys: keys.iter().map(|k| k.to_string()).collect(),
            wake_adapters: Vec::new(),
            wake_refreshed_at: None,
        }
    }

    #[test]
    fn find_matches_host_id_or_entry_key() {
        let store = PairedHostStore::new(None);
        store.save(host("ID-1", &["manual:pc:48443"])).unwrap();

        assert!(store.find(Some("id-1"), "mdns:other").is_some());
        assert!(store.find(None, "manual:pc:48443").is_some());
        assert!(store.find(Some("id-2"), "mdns:id-2").is_none());
    }

    #[test]
    fn save_replaces_and_remove_deletes() {
        let path =
            std::env::temp_dir().join(format!("hyperharbor-paired-{}.json", std::process::id()));
        let _ = std::fs::remove_file(&path);

        let store = PairedHostStore::new(Some(path.clone()));
        store.save(host("ID-1", &["a"])).unwrap();
        store.save(host("id-1", &["b"])).unwrap();

        let reloaded = PairedHostStore::new(Some(path.clone()));
        assert_eq!(
            reloaded.find(Some("ID-1"), "").unwrap().entry_keys,
            vec!["b"]
        );
        assert_eq!(
            reloaded.find(None, "b").unwrap().certificate_hash(),
            Some([0xAB; 32])
        );

        reloaded.remove("ID-1").unwrap();
        assert!(PairedHostStore::new(Some(path.clone()))
            .find(Some("ID-1"), "")
            .is_none());
        let _ = std::fs::remove_file(&path);
    }
}
