use std::collections::HashMap;
use std::net::IpAddr;
use std::path::PathBuf;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::error::ClientError;

/// DNS-SD service type advertised by HyperHarbor hosts.
pub const SERVICE_TYPE: &str = "_hyperharbor._tcp.local.";

/// Default API port, matching the host's Api:Port setting.
pub const DEFAULT_PORT: u16 = 48443;

// TXT record keys published by the host (see ServiceAdvertisement.cs).
pub const TXT_HOST_ID: &str = "id";
pub const TXT_API_VERSION: &str = "api";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum HostSource {
    Discovered,
    Manual,
}

/// A host shown in the client, either discovered over mDNS or added manually.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HostEntry {
    /// Stable key used by the frontend to refer to this entry.
    pub key: String,
    pub display_name: String,
    pub host_id: Option<String>,
    pub host_name: Option<String>,
    pub addresses: Vec<String>,
    pub port: u16,
    pub api_version: Option<String>,
    pub source: HostSource,
    /// True when the host runs on this machine and can be reached over loopback.
    pub is_local: bool,
}

/// A manually added host as stored on disk.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ManualHost {
    pub address: String,
    pub port: u16,
}

impl ManualHost {
    /// Parses "host", "host:port", "1.2.3.4:port", or "[v6]:port".
    pub fn parse(input: &str) -> Result<Self, ClientError> {
        let input = input.trim();
        if input.is_empty() || input.contains(char::is_whitespace) || input.contains('/') {
            return Err(ClientError::InvalidAddress);
        }

        if let Ok(ip) = input.parse::<IpAddr>() {
            return Ok(Self {
                address: ip.to_string(),
                port: DEFAULT_PORT,
            });
        }

        if let Some(rest) = input.strip_prefix('[') {
            let (address, port) = rest.split_once("]:").ok_or(ClientError::InvalidAddress)?;
            let ip: std::net::Ipv6Addr =
                address.parse().map_err(|_| ClientError::InvalidAddress)?;
            return Ok(Self {
                address: ip.to_string(),
                port: parse_port(port)?,
            });
        }

        let (address, port) = match input.rsplit_once(':') {
            Some((address, port)) => (address, parse_port(port)?),
            None => (input, DEFAULT_PORT),
        };

        let valid_name = !address.is_empty()
            && address.split('.').all(|label| {
                !label.is_empty()
                    && label.len() <= 63
                    && label.chars().all(|c| c.is_ascii_alphanumeric() || c == '-')
            });
        if !valid_name {
            return Err(ClientError::InvalidAddress);
        }

        Ok(Self {
            address: address.to_ascii_lowercase(),
            port,
        })
    }

    pub fn key(&self) -> String {
        format!("manual:{}:{}", self.address, self.port)
    }

    pub fn to_entry(&self, local_host_name: &str) -> HostEntry {
        HostEntry {
            key: self.key(),
            display_name: self.address.clone(),
            host_id: None,
            host_name: None,
            addresses: vec![self.address.clone()],
            port: self.port,
            api_version: None,
            source: HostSource::Manual,
            is_local: is_local_address(&self.address, local_host_name),
        }
    }
}

fn parse_port(value: &str) -> Result<u16, ClientError> {
    match value.parse::<u16>() {
        Ok(port) if port > 0 => Ok(port),
        _ => Err(ClientError::InvalidAddress),
    }
}

/// Strips a trailing ".local." or ".local" and lowercases, so "PC.local." matches "pc".
pub fn normalize_host_name(name: &str) -> String {
    let name = name.trim_end_matches('.');
    let name = name.strip_suffix(".local").unwrap_or(name);
    name.to_ascii_lowercase()
}

pub fn is_local_host_name(name: &str, local_host_name: &str) -> bool {
    !local_host_name.is_empty() && normalize_host_name(name) == normalize_host_name(local_host_name)
}

pub fn is_local_address(address: &str, local_host_name: &str) -> bool {
    match address.parse::<IpAddr>() {
        Ok(ip) => ip.is_loopback(),
        Err(_) => {
            address.eq_ignore_ascii_case("localhost")
                || is_local_host_name(address, local_host_name)
        }
    }
}

/// Builds the display label for a discovered instance, for example "GAMING-PC" from
/// "GAMING-PC._hyperharbor._tcp.local.".
pub fn instance_label(full_name: &str) -> String {
    full_name
        .strip_suffix(SERVICE_TYPE)
        .map(|label| label.trim_end_matches('.'))
        .filter(|label| !label.is_empty())
        .unwrap_or(full_name)
        .to_string()
}

/// Holds discovered and manual hosts. Manual hosts are persisted to a JSON file.
pub struct HostRegistry {
    local_host_name: String,
    storage_path: Option<PathBuf>,
    discovered: Mutex<HashMap<String, HostEntry>>,
    manual: Mutex<Vec<ManualHost>>,
}

impl HostRegistry {
    pub fn new(local_host_name: String, storage_path: Option<PathBuf>) -> Self {
        let manual = storage_path
            .as_ref()
            .and_then(|path| std::fs::read_to_string(path).ok())
            .and_then(|text| serde_json::from_str::<Vec<ManualHost>>(&text).ok())
            .unwrap_or_default();

        Self {
            local_host_name,
            storage_path,
            discovered: Mutex::new(HashMap::new()),
            manual: Mutex::new(manual),
        }
    }

    pub fn local_host_name(&self) -> &str {
        &self.local_host_name
    }

    /// All hosts, local first, then by name.
    pub fn list(&self) -> Vec<HostEntry> {
        let mut hosts: Vec<HostEntry> = self.discovered.lock().unwrap().values().cloned().collect();
        hosts.extend(
            self.manual
                .lock()
                .unwrap()
                .iter()
                .map(|m| m.to_entry(&self.local_host_name)),
        );
        hosts.sort_by(|a, b| {
            b.is_local.cmp(&a.is_local).then_with(|| {
                a.display_name
                    .to_lowercase()
                    .cmp(&b.display_name.to_lowercase())
            })
        });
        hosts
    }

    pub fn get(&self, key: &str) -> Option<HostEntry> {
        self.list().into_iter().find(|host| host.key == key)
    }

    /// Records a discovered host. Returns true when the visible list changed.
    pub fn upsert_discovered(&self, full_name: &str, entry: HostEntry) -> bool {
        let mut discovered = self.discovered.lock().unwrap();
        discovered.insert(full_name.to_string(), entry.clone()) != Some(entry)
    }

    pub fn remove_discovered(&self, full_name: &str) -> bool {
        self.discovered.lock().unwrap().remove(full_name).is_some()
    }

    pub fn add_manual(&self, input: &str) -> Result<HostEntry, ClientError> {
        let host = ManualHost::parse(input)?;
        let entry = host.to_entry(&self.local_host_name);
        {
            let mut manual = self.manual.lock().unwrap();
            if !manual.contains(&host) {
                manual.push(host);
            }
        }
        self.save()?;
        Ok(entry)
    }

    pub fn remove_manual(&self, key: &str) -> Result<(), ClientError> {
        self.manual.lock().unwrap().retain(|host| host.key() != key);
        self.save()
    }

    fn save(&self) -> Result<(), ClientError> {
        let Some(path) = &self.storage_path else {
            return Ok(());
        };
        let text = serde_json::to_string_pretty(&*self.manual.lock().unwrap())
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

    #[test]
    fn parse_accepts_names_ips_and_ports() {
        assert_eq!(
            ManualHost::parse("Gaming-PC").unwrap(),
            ManualHost {
                address: "gaming-pc".into(),
                port: DEFAULT_PORT
            }
        );
        assert_eq!(
            ManualHost::parse("pc.lan:5000").unwrap(),
            ManualHost {
                address: "pc.lan".into(),
                port: 5000
            }
        );
        assert_eq!(
            ManualHost::parse("192.168.1.10").unwrap(),
            ManualHost {
                address: "192.168.1.10".into(),
                port: DEFAULT_PORT
            }
        );
        assert_eq!(
            ManualHost::parse("192.168.1.10:49000").unwrap(),
            ManualHost {
                address: "192.168.1.10".into(),
                port: 49000
            }
        );
        assert_eq!(
            ManualHost::parse("::1").unwrap(),
            ManualHost {
                address: "::1".into(),
                port: DEFAULT_PORT
            }
        );
        assert_eq!(
            ManualHost::parse("[fe80::1]:49000").unwrap(),
            ManualHost {
                address: "fe80::1".into(),
                port: 49000
            }
        );
    }

    #[test]
    fn parse_rejects_invalid_input() {
        for input in [
            "",
            "  ",
            "http://pc",
            "pc:0",
            "pc:70000",
            "pc name",
            "pc..lan",
            "[::1]",
            "bad_char",
        ] {
            assert!(
                ManualHost::parse(input).is_err(),
                "{input} should be rejected"
            );
        }
    }

    #[test]
    fn local_detection_handles_loopback_and_own_name() {
        assert!(is_local_address("127.0.0.1", "PC"));
        assert!(is_local_address("::1", "PC"));
        assert!(is_local_address("localhost", "PC"));
        assert!(is_local_address("pc.local", "PC"));
        assert!(!is_local_address("192.168.1.10", "PC"));
        assert!(!is_local_address("other", "PC"));
        assert!(is_local_host_name("PC.local.", "pc"));
        assert!(!is_local_host_name("PC.local.", ""));
    }

    #[test]
    fn instance_label_strips_service_type() {
        assert_eq!(
            instance_label("GAMING-PC._hyperharbor._tcp.local."),
            "GAMING-PC"
        );
        assert_eq!(instance_label("odd"), "odd");
    }

    #[test]
    fn registry_lists_local_first_and_persists_manual_hosts() {
        let path =
            std::env::temp_dir().join(format!("hyperharbor-test-{}.json", std::process::id()));
        let _ = std::fs::remove_file(&path);

        let registry = HostRegistry::new("pc".into(), Some(path.clone()));
        registry.add_manual("zeta").unwrap();
        registry.add_manual("localhost").unwrap();
        registry.add_manual("zeta").unwrap();

        let keys: Vec<String> = registry.list().into_iter().map(|h| h.key).collect();
        assert_eq!(keys, vec!["manual:localhost:48443", "manual:zeta:48443"]);

        let reloaded = HostRegistry::new("pc".into(), Some(path.clone()));
        assert_eq!(reloaded.list().len(), 2);

        reloaded.remove_manual("manual:zeta:48443").unwrap();
        assert_eq!(
            HostRegistry::new("pc".into(), Some(path.clone()))
                .list()
                .len(),
            1
        );
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn upsert_reports_changes_only() {
        let registry = HostRegistry::new("pc".into(), None);
        let entry = ManualHost::parse("other").unwrap().to_entry("pc");
        assert!(registry.upsert_discovered("x", entry.clone()));
        assert!(!registry.upsert_discovered("x", entry));
        assert!(registry.remove_discovered("x"));
        assert!(!registry.remove_discovered("x"));
    }
}
