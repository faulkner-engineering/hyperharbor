//! Removes virtual cameras left behind by a client that did not shut down cleanly.
//!
//! Cameras are created with session lifetime, so Windows removes them when their creator exits,
//! and leftovers are rare. This is the backstop. It removes only what the ledger proves this
//! client made and whose creating process is gone; a camera that merely has a matching name (and
//! no ledger entry) might belong to another program or another install, so it is left alone and
//! reported.

use super::ledger::Ledger;
use super::naming;
use super::resolve::VideoDevice;
use super::CameraError;

/// The Windows side of cleanup.
pub trait VirtualCameraRegistry {
    /// The video devices Windows lists, virtual cameras included.
    fn list(&self) -> Result<Vec<VideoDevice>, CameraError>;
    /// Removes the virtual camera created as `created_name` (without the suffix Windows adds).
    fn remove(&self, created_name: &str) -> Result<(), CameraError>;
}

/// Whether a process exists. Windows: open the process and check it has not exited.
pub trait ProcessProbe {
    fn is_alive(&self, pid: u32) -> bool;
}

#[derive(Debug, Default, PartialEq, Eq)]
pub struct ReconcileReport {
    /// Cameras removed because their creator is gone.
    pub removed: Vec<String>,
    /// Cameras left because a live process (another client window) owns them.
    pub kept: Vec<String>,
    /// Cameras with our name pattern but no ledger entry; left alone.
    pub unknown: Vec<String>,
    /// Ledger entries dropped because the camera no longer exists or was just removed.
    pub forgotten: Vec<String>,
    /// Removals that failed; the camera stays in the ledger so the next start tries again.
    pub failed: Vec<(String, String)>,
}

/// Compares the listed cameras with the ledger and removes the orphans. `own_pid` is this
/// process: a camera it already owns is never an orphan.
pub fn reconcile(
    registry: &dyn VirtualCameraRegistry,
    ledger: &mut Ledger,
    probe: &dyn ProcessProbe,
    own_pid: u32,
) -> Result<ReconcileReport, CameraError> {
    let devices = registry.list()?;
    let mut report = ReconcileReport::default();

    for device in &devices {
        if naming::owned_label(&device.name).is_none() {
            continue;
        }
        let created_name = device
            .name
            .strip_suffix(naming::OS_SUFFIX)
            .unwrap_or(&device.name)
            .to_string();
        match ledger.find(&created_name).cloned() {
            None => report.unknown.push(created_name),
            Some(entry) if entry.client_pid == own_pid || probe.is_alive(entry.client_pid) => {
                report.kept.push(created_name);
            }
            Some(_) => match registry.remove(&created_name) {
                Ok(()) => {
                    ledger.remove(&created_name)?;
                    report.removed.push(created_name.clone());
                    report.forgotten.push(created_name);
                }
                Err(error) => report.failed.push((created_name, error.to_string())),
            },
        }
    }

    // Entries whose camera is already gone (Windows removed it with its creator).
    let stale: Vec<String> = ledger
        .entries()
        .iter()
        .filter(|entry| {
            !devices
                .iter()
                .any(|d| naming::names_match(&d.name, &entry.name))
        })
        .map(|entry| entry.name.clone())
        .collect();
    for name in stale {
        ledger.remove(&name)?;
        if !report.forgotten.contains(&name) {
            report.forgotten.push(name);
        }
    }

    Ok(report)
}

#[cfg(test)]
mod tests {
    use super::super::ledger::LedgerEntry;
    use super::*;
    use std::cell::RefCell;
    use std::collections::HashSet;
    use std::path::PathBuf;

    const OWN: u32 = 100;

    struct FakeRegistry {
        devices: Vec<VideoDevice>,
        removed: RefCell<Vec<String>>,
        failing: HashSet<String>,
    }

    impl FakeRegistry {
        fn new(names: &[&str]) -> Self {
            Self {
                devices: names
                    .iter()
                    .map(|name| VideoDevice {
                        name: (*name).to_string(),
                        symbolic_link: format!(r"\\?\swd#{name}"),
                    })
                    .collect(),
                removed: RefCell::new(Vec::new()),
                failing: HashSet::new(),
            }
        }
    }

    impl VirtualCameraRegistry for FakeRegistry {
        fn list(&self) -> Result<Vec<VideoDevice>, CameraError> {
            Ok(self.devices.clone())
        }
        fn remove(&self, created_name: &str) -> Result<(), CameraError> {
            if self.failing.contains(created_name) {
                return Err(CameraError::Removal("access denied".into()));
            }
            self.removed.borrow_mut().push(created_name.to_string());
            Ok(())
        }
    }

    struct Alive(Vec<u32>);
    impl ProcessProbe for Alive {
        fn is_alive(&self, pid: u32) -> bool {
            self.0.contains(&pid)
        }
    }

    fn ledger_with(tag: &str, entries: &[(&str, u32)]) -> Ledger {
        let dir: PathBuf =
            std::env::temp_dir().join(format!("hh-reconcile-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let mut ledger = Ledger::load(&dir.join("camera-ledger.json"));
        for (name, pid) in entries {
            ledger
                .add(LedgerEntry {
                    name: (*name).to_string(),
                    client_pid: *pid,
                    created_unix: 1,
                })
                .unwrap();
        }
        ledger
    }

    const WORK: &str = "HyperHarbor Camera (Work)";
    const GAMES: &str = "HyperHarbor Camera (Games)";
    const WORK_LISTED: &str = "HyperHarbor Camera (Work) (Windows Virtual Camera)";
    const GAMES_LISTED: &str = "HyperHarbor Camera (Games) (Windows Virtual Camera)";

    #[test]
    fn a_camera_whose_creator_is_gone_is_removed_and_forgotten() {
        let registry = FakeRegistry::new(&[WORK_LISTED]);
        let mut ledger = ledger_with("dead", &[(WORK, 7)]);
        let report = reconcile(&registry, &mut ledger, &Alive(vec![]), OWN).unwrap();
        assert_eq!(report.removed, vec![WORK.to_string()]);
        assert_eq!(*registry.removed.borrow(), vec![WORK.to_string()]);
        assert!(ledger.entries().is_empty());
    }

    #[test]
    fn a_camera_owned_by_a_live_process_is_kept() {
        let registry = FakeRegistry::new(&[WORK_LISTED]);
        let mut ledger = ledger_with("alive", &[(WORK, 7)]);
        let report = reconcile(&registry, &mut ledger, &Alive(vec![7]), OWN).unwrap();
        assert_eq!(report.kept, vec![WORK.to_string()]);
        assert!(registry.removed.borrow().is_empty());
        assert_eq!(ledger.entries().len(), 1);
    }

    #[test]
    fn this_clients_own_cameras_are_never_orphans() {
        let registry = FakeRegistry::new(&[WORK_LISTED]);
        let mut ledger = ledger_with("own", &[(WORK, OWN)]);
        let report = reconcile(&registry, &mut ledger, &Alive(vec![]), OWN).unwrap();
        assert_eq!(report.kept, vec![WORK.to_string()]);
        assert!(registry.removed.borrow().is_empty());
    }

    #[test]
    fn a_matching_name_without_a_ledger_entry_is_left_alone() {
        let registry = FakeRegistry::new(&[GAMES_LISTED]);
        let mut ledger = ledger_with("unknown", &[]);
        let report = reconcile(&registry, &mut ledger, &Alive(vec![]), OWN).unwrap();
        assert_eq!(report.unknown, vec![GAMES.to_string()]);
        assert!(registry.removed.borrow().is_empty());
    }

    #[test]
    fn other_programs_cameras_are_never_touched() {
        let registry = FakeRegistry::new(&["Integrated Camera", "OBS Virtual Camera"]);
        let mut ledger = ledger_with("others", &[]);
        let report = reconcile(&registry, &mut ledger, &Alive(vec![]), OWN).unwrap();
        assert_eq!(report, ReconcileReport::default());
        assert!(registry.removed.borrow().is_empty());
    }

    #[test]
    fn ledger_entries_for_cameras_that_are_gone_are_dropped() {
        let registry = FakeRegistry::new(&[]);
        let mut ledger = ledger_with("stale", &[(WORK, 7)]);
        let report = reconcile(&registry, &mut ledger, &Alive(vec![]), OWN).unwrap();
        assert_eq!(report.forgotten, vec![WORK.to_string()]);
        assert!(ledger.entries().is_empty());
        assert!(registry.removed.borrow().is_empty());
    }

    #[test]
    fn one_failed_removal_does_not_stop_the_others_and_stays_in_the_ledger() {
        let mut registry = FakeRegistry::new(&[WORK_LISTED, GAMES_LISTED]);
        registry.failing.insert(WORK.to_string());
        let mut ledger = ledger_with("failure", &[(WORK, 7), (GAMES, 8)]);
        let report = reconcile(&registry, &mut ledger, &Alive(vec![]), OWN).unwrap();
        assert_eq!(report.removed, vec![GAMES.to_string()]);
        assert_eq!(report.failed.len(), 1);
        assert_eq!(report.failed[0].0, WORK);
        assert!(ledger.find(WORK).is_some());
        assert!(ledger.find(GAMES).is_none());
    }

    #[test]
    fn a_mix_is_sorted_correctly() {
        let registry = FakeRegistry::new(&[WORK_LISTED, GAMES_LISTED, "HD Pro Webcam C920"]);
        let mut ledger = ledger_with("mix", &[(WORK, 7), (GAMES, 8)]);
        let report = reconcile(&registry, &mut ledger, &Alive(vec![8]), OWN).unwrap();
        assert_eq!(report.removed, vec![WORK.to_string()]);
        assert_eq!(report.kept, vec![GAMES.to_string()]);
    }

    struct BrokenRegistry;
    impl VirtualCameraRegistry for BrokenRegistry {
        fn list(&self) -> Result<Vec<VideoDevice>, CameraError> {
            Err(CameraError::Enumeration("down".into()))
        }
        fn remove(&self, _: &str) -> Result<(), CameraError> {
            Ok(())
        }
    }

    #[test]
    fn when_the_cameras_cannot_be_listed_nothing_is_forgotten() {
        let mut ledger = ledger_with("broken", &[(WORK, 7)]);
        assert!(reconcile(&BrokenRegistry, &mut ledger, &Alive(vec![]), OWN).is_err());
        assert_eq!(ledger.entries().len(), 1);
    }
}
