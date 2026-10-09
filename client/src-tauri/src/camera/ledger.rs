//! A record of the virtual cameras this client has created, kept in a file so the next start can
//! tell cameras it made (and can remove) from cameras it did not.

use super::CameraError;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LedgerEntry {
    /// The friendly name the camera was created with (without the suffix Windows adds).
    pub name: String,
    /// The client process that created it.
    pub client_pid: u32,
    pub created_unix: u64,
}

#[derive(Debug, Default, Serialize, Deserialize)]
struct LedgerFile {
    cameras: Vec<LedgerEntry>,
}

/// The ledger file. Every change is written at once, so a crash loses nothing.
pub struct Ledger {
    path: PathBuf,
    entries: Vec<LedgerEntry>,
}

impl Ledger {
    /// Reads the ledger. A missing or unreadable file is an empty ledger: the ledger only ever
    /// narrows what is removed, so starting empty is the safe way to fail.
    pub fn load(path: &Path) -> Self {
        let entries = std::fs::read(path)
            .ok()
            .and_then(|bytes| serde_json::from_slice::<LedgerFile>(&bytes).ok())
            .map(|file| file.cameras)
            .unwrap_or_default();
        Self {
            path: path.to_path_buf(),
            entries,
        }
    }

    pub fn entries(&self) -> &[LedgerEntry] {
        &self.entries
    }

    pub fn find(&self, name: &str) -> Option<&LedgerEntry> {
        self.entries.iter().find(|entry| entry.name == name)
    }

    /// Records a camera, replacing an entry of the same name.
    pub fn add(&mut self, entry: LedgerEntry) -> Result<(), CameraError> {
        self.entries.retain(|existing| existing.name != entry.name);
        self.entries.push(entry);
        self.save()
    }

    pub fn remove(&mut self, name: &str) -> Result<(), CameraError> {
        self.entries.retain(|entry| entry.name != name);
        self.save()
    }

    fn save(&self) -> Result<(), CameraError> {
        let file = LedgerFile {
            cameras: self.entries.clone(),
        };
        let json =
            serde_json::to_vec_pretty(&file).map_err(|e| CameraError::Ledger(e.to_string()))?;
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| CameraError::Ledger(e.to_string()))?;
        }
        let temporary = self.path.with_extension("json.tmp");
        std::fs::write(&temporary, json).map_err(|e| CameraError::Ledger(e.to_string()))?;
        std::fs::rename(&temporary, &self.path).map_err(|e| CameraError::Ledger(e.to_string()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(name: &str, pid: u32) -> LedgerEntry {
        LedgerEntry {
            name: name.to_string(),
            client_pid: pid,
            created_unix: 1,
        }
    }

    fn temp_path(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("hh-ledger-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        dir.join("camera-ledger.json")
    }

    #[test]
    fn entries_survive_a_reload() {
        let path = temp_path("reload");
        let mut ledger = Ledger::load(&path);
        ledger.add(entry("HyperHarbor Camera (Work)", 10)).unwrap();
        ledger.add(entry("HyperHarbor Camera (Games)", 11)).unwrap();
        let reloaded = Ledger::load(&path);
        assert_eq!(reloaded.entries().len(), 2);
        assert_eq!(
            reloaded
                .find("HyperHarbor Camera (Work)")
                .unwrap()
                .client_pid,
            10
        );
    }

    #[test]
    fn adding_the_same_name_replaces_the_entry() {
        let path = temp_path("replace");
        let mut ledger = Ledger::load(&path);
        ledger.add(entry("A", 1)).unwrap();
        ledger.add(entry("A", 2)).unwrap();
        assert_eq!(ledger.entries().len(), 1);
        assert_eq!(ledger.find("A").unwrap().client_pid, 2);
    }

    #[test]
    fn removing_an_entry_is_saved() {
        let path = temp_path("remove");
        let mut ledger = Ledger::load(&path);
        ledger.add(entry("A", 1)).unwrap();
        ledger.remove("A").unwrap();
        assert!(Ledger::load(&path).entries().is_empty());
    }

    #[test]
    fn a_missing_or_damaged_file_is_an_empty_ledger() {
        assert!(Ledger::load(&temp_path("missing")).entries().is_empty());
        let path = temp_path("damaged");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, b"{ not json").unwrap();
        assert!(Ledger::load(&path).entries().is_empty());
    }
}
