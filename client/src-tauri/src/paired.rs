//! Hosts this client has paired with, persisted in the app config directory. Nothing here is
//! secret: the host certificate is public and the client key lives in the credential store.

use std::path::PathBuf;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::error::ClientError;
use crate::hosts::{is_local_host_name, sort_hosts, HostEntry, HostSource, DEFAULT_PORT};
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
    /// Last known addresses, port, and host name, so the host stays listed (and can be woken)
    /// while it is asleep and not answering mDNS.
    #[serde(default)]
    pub addresses: Vec<String>,
    #[serde(default = "default_port")]
    pub port: u16,
    #[serde(default)]
    pub host_name: Option<String>,
}

fn default_port() -> u16 {
    DEFAULT_PORT
}

impl PairedHost {
    pub fn certificate_hash(&self) -> Option<[u8; 32]> {
        hex::decode(&self.host_certificate_fingerprint)
            .ok()?
            .try_into()
            .ok()
    }

    /// A host list entry for a paired host that is not currently discovered. Uses the same key
    /// as its discovered entry, so the UI selection survives the host going to sleep.
    pub fn remembered_entry(&self, local_host_name: &str) -> HostEntry {
        HostEntry {
            key: format!("mdns:{}", self.host_id),
            display_name: self.display_name.clone(),
            host_id: Some(self.host_id.clone()),
            host_name: self.host_name.clone(),
            addresses: self.addresses.clone(),
            port: self.port,
            api_version: None,
            source: HostSource::Remembered,
            is_local: self
                .host_name
                .as_deref()
                .is_some_and(|name| is_local_host_name(name, local_host_name)),
            paired: true,
            can_wake: !self.wake_adapters.is_empty(),
        }
    }
}

/// Adds a remembered entry for every paired host that is not already in the list.
pub fn add_remembered(hosts: &mut Vec<HostEntry>, paired: &[PairedHost], local_host_name: &str) {
    for host in paired {
        let present = hosts.iter().any(|entry| {
            entry
                .host_id
                .as_deref()
                .is_some_and(|id| id.eq_ignore_ascii_case(&host.host_id))
                || host.entry_keys.contains(&entry.key)
        });
        if !present {
            hosts.push(host.remembered_entry(local_host_name));
        }
    }
    sort_hosts(hosts);
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

    pub fn all(&self) -> Vec<PairedHost> {
        self.hosts.lock().unwrap().clone()
    }

    /// Records where a reachable host was found. Returns true when anything changed.
    pub fn update_endpoint(&self, host_id: &str, entry: &HostEntry) -> Result<bool, ClientError> {
        let changed = {
            let mut hosts = self.hosts.lock().unwrap();
            let Some(host) = hosts
                .iter_mut()
                .find(|h| h.host_id.eq_ignore_ascii_case(host_id))
            else {
                return Ok(false);
            };

            let before = host.clone();
            if !entry.addresses.is_empty() {
                host.addresses = entry.addresses.clone();
            }
            host.port = entry.port;
            if entry.host_name.is_some() {
                host.host_name = entry.host_name.clone();
            }
            // Manual entries are named by the typed address; only discovery knows the real name.
            if entry.source == HostSource::Discovered {
                host.display_name = entry.display_name.clone();
            }
            *host != before
        };

        if changed {
            self.persist()?;
        }
        Ok(changed)
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
            addresses: Vec::new(),
            port: DEFAULT_PORT,
            host_name: None,
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

    fn discovered(id: &str, name: &str, addresses: &[&str]) -> HostEntry {
        HostEntry {
            key: format!("mdns:{id}"),
            display_name: name.into(),
            host_id: Some(id.into()),
            host_name: Some(format!("{name}.local")),
            addresses: addresses.iter().map(|a| a.to_string()).collect(),
            port: 48443,
            api_version: Some("1.0.0".into()),
            source: HostSource::Discovered,
            is_local: false,
            paired: false,
            can_wake: false,
        }
    }

    #[test]
    fn sleeping_paired_host_stays_listed_under_the_same_key() {
        let store = PairedHostStore::new(None);
        store.save(host("ID-1", &["mdns:ID-1"])).unwrap();
        store
            .update_endpoint("ID-1", &discovered("ID-1", "DESKTOP", &["192.168.1.20"]))
            .unwrap();
        store
            .update_wake(
                "ID-1",
                vec![crate::wake::WakeAdapter {
                    name: "Ethernet".into(),
                    mac_address: "90-2E-16-66-C5-AE".into(),
                    ipv4_address: "192.168.1.20".into(),
                    broadcast_address: "192.168.1.255".into(),
                }],
                1,
            )
            .unwrap();

        // The host went to sleep: discovery no longer lists it.
        let mut hosts = Vec::new();
        add_remembered(&mut hosts, &store.all(), "laptop");

        let entry = hosts.single();
        assert_eq!(entry.key, "mdns:ID-1");
        assert_eq!(entry.display_name, "DESKTOP");
        assert_eq!(entry.addresses, vec!["192.168.1.20"]);
        assert_eq!(entry.source, HostSource::Remembered);
        assert!(entry.paired && entry.can_wake && !entry.is_local);
    }

    #[test]
    fn remembered_entry_is_not_added_when_host_is_present() {
        let store = PairedHostStore::new(None);
        store.save(host("ID-1", &["manual:pc:48443"])).unwrap();
        store.save(host("ID-2", &[])).unwrap();

        let mut manual = discovered("x", "pc", &["pc"]);
        manual.key = "manual:pc:48443".into();
        manual.host_id = None;
        manual.source = HostSource::Manual;
        let mut hosts = vec![manual, discovered("id-2", "Other", &["10.0.0.2"])];

        add_remembered(&mut hosts, &store.all(), "laptop");

        assert_eq!(hosts.len(), 2);
        assert!(hosts.iter().all(|h| h.source != HostSource::Remembered));
    }

    #[test]
    fn remembered_local_host_is_detected_from_host_name() {
        let mut paired = host("ID-1", &[]);
        paired.host_name = Some("LAPTOP.local".into());
        assert!(paired.remembered_entry("laptop").is_local);
    }

    #[test]
    fn update_endpoint_keeps_discovered_name_over_manual_address() {
        let store = PairedHostStore::new(None);
        store.save(host("ID-1", &[])).unwrap();
        assert!(store
            .update_endpoint("ID-1", &discovered("ID-1", "DESKTOP", &["192.168.1.20"]))
            .unwrap());
        assert!(!store
            .update_endpoint("ID-1", &discovered("ID-1", "DESKTOP", &["192.168.1.20"]))
            .unwrap());

        let mut manual = discovered("ID-1", "192.168.1.21", &["192.168.1.21"]);
        manual.source = HostSource::Manual;
        store.update_endpoint("ID-1", &manual).unwrap();

        let stored = store.find(Some("ID-1"), "").unwrap();
        assert_eq!(stored.display_name, "DESKTOP");
        assert_eq!(stored.addresses, vec!["192.168.1.21"]);
    }

    trait Single {
        fn single(&self) -> &HostEntry;
    }

    impl Single for Vec<HostEntry> {
        fn single(&self) -> &HostEntry {
            assert_eq!(self.len(), 1);
            &self[0]
        }
    }
}
