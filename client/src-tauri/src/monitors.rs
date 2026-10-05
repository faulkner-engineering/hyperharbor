//! Which of this device's monitors a Remote Desktop session uses.
//!
//! mstsc numbers monitors in its own order (`mstsc /l` shows it): the displays attached to the
//! desktop, in EnumDisplayDevices order. That differs from EnumDisplayMonitors and from the numbers
//! in Windows display settings, and it changes when monitors are added or rearranged. The saved
//! choice therefore names monitors by their device path, and the IDs are worked out at each connect.
//! Each VM has its own choice.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::error::ClientError;

/// A monitor attached to the desktop, in physical pixels on the virtual screen.
#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Monitor {
    /// Stable identity: the monitor's device path, or the GDI name when Windows reports none.
    pub key: String,
    /// The ID `mstsc /l` shows and `selectedmonitors` takes.
    pub mstsc_id: u32,
    /// The monitor's own name (from its EDID), or a generic name.
    pub name: String,
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    pub primary: bool,
}

/// Which monitors Connect uses.
#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum MonitorMode {
    /// mstsc's default: one monitor.
    #[default]
    Single,
    All,
    Selected,
}

/// The saved choice. `selected` holds monitor keys and is used only in Selected mode.
#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MonitorChoice {
    pub mode: MonitorMode,
    #[serde(default)]
    pub selected: Vec<String>,
}

/// What goes into the .rdp file.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum MonitorLayout {
    Single,
    All,
    Selected(Vec<u32>),
}

impl MonitorLayout {
    /// The .rdp settings for this layout. Multiple monitors apply only in full screen, which is
    /// mstsc's default screen mode.
    pub fn rdp_lines(&self) -> Vec<String> {
        match self {
            MonitorLayout::Single => Vec::new(),
            MonitorLayout::All => vec!["use multimon:i:1".to_string()],
            MonitorLayout::Selected(ids) => vec![
                "use multimon:i:1".to_string(),
                format!(
                    "selectedmonitors:s:{}",
                    ids.iter().map(u32::to_string).collect::<Vec<_>>().join(",")
                ),
            ],
        }
    }
}

impl MonitorChoice {
    /// The layout for the monitors attached now. Saved monitors that are not attached are skipped;
    /// if none of them is, the session uses one monitor. The local primary monitor comes first when
    /// it is selected, the others follow in mstsc's order.
    pub fn resolve(&self, monitors: &[Monitor]) -> MonitorLayout {
        match self.mode {
            MonitorMode::Single => MonitorLayout::Single,
            MonitorMode::All => MonitorLayout::All,
            MonitorMode::Selected => {
                let mut chosen: Vec<&Monitor> = monitors
                    .iter()
                    .filter(|monitor| self.selected.contains(&monitor.key))
                    .collect();
                if chosen.is_empty() {
                    return MonitorLayout::Single;
                }
                chosen.sort_by_key(|monitor| (!monitor.primary, monitor.mstsc_id));
                MonitorLayout::Selected(chosen.iter().map(|monitor| monitor.mstsc_id).collect())
            }
        }
    }
}

/// The monitor choice of each VM, in client-settings.json under the app's config directory. It
/// stays on this device because it names this device's monitors. VMs are keyed by the paired host's
/// ID and the VM's ID, so a host that changes its address keeps its choices. A VM without an entry
/// uses one monitor.
pub struct MonitorChoiceStore {
    path: Option<PathBuf>,
    choices: Mutex<BTreeMap<String, MonitorChoice>>,
}

#[derive(Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SettingsFile {
    #[serde(default)]
    vm_monitors: BTreeMap<String, MonitorChoice>,
}

fn vm_key(host_id: &str, vm_id: &str) -> String {
    format!(
        "{}/{}",
        host_id.to_ascii_lowercase(),
        vm_id.to_ascii_lowercase()
    )
}

impl MonitorChoiceStore {
    pub fn new(path: Option<PathBuf>) -> Self {
        let choices = path
            .as_ref()
            .and_then(|path| std::fs::read_to_string(path).ok())
            .and_then(|text| serde_json::from_str::<SettingsFile>(&text).ok())
            .map(|file| file.vm_monitors)
            .unwrap_or_default();
        Self {
            path,
            choices: Mutex::new(choices),
        }
    }

    pub fn get(&self, host_id: &str, vm_id: &str) -> MonitorChoice {
        self.choices
            .lock()
            .unwrap()
            .get(&vm_key(host_id, vm_id))
            .cloned()
            .unwrap_or_default()
    }

    /// Saves the VM's choice. One monitor is the default, so it removes the VM's entry.
    pub fn set(
        &self,
        host_id: &str,
        vm_id: &str,
        choice: MonitorChoice,
    ) -> Result<(), ClientError> {
        if !crate::hosts::is_guid(vm_id) {
            return Err(ClientError::InvalidVmId);
        }
        if choice.mode == MonitorMode::Selected && choice.selected.is_empty() {
            return Err(ClientError::InvalidRequest(
                "choose at least one monitor".into(),
            ));
        }
        let mut choices = self.choices.lock().unwrap();
        let mut updated = choices.clone();
        if choice.mode == MonitorMode::Single {
            updated.remove(&vm_key(host_id, vm_id));
        } else {
            updated.insert(vm_key(host_id, vm_id), choice);
        }
        if let Some(path) = &self.path {
            let text = serde_json::to_string_pretty(&SettingsFile {
                vm_monitors: updated.clone(),
            })
            .map_err(|e| ClientError::Storage(e.to_string()))?;
            if let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent).map_err(|e| ClientError::Storage(e.to_string()))?;
            }
            std::fs::write(path, text).map_err(|e| ClientError::Storage(e.to_string()))?;
        }
        *choices = updated;
        Ok(())
    }
}

/// The monitors attached to the desktop, in mstsc's order.
pub fn list() -> Vec<Monitor> {
    let names = win32::monitor_names();
    win32::attached_displays()
        .into_iter()
        .enumerate()
        .map(|(index, display)| {
            let target = names.iter().find(|name| name.gdi_name == display.gdi_name);
            let path = target.map(|target| target.path.clone()).unwrap_or_default();
            let name = match target {
                Some(target) if !target.name.is_empty() => target.name.clone(),
                Some(target) if target.internal => "Built-in display".to_string(),
                _ => format!("Display {}", index + 1),
            };
            Monitor {
                key: if path.is_empty() {
                    display.gdi_name.clone()
                } else {
                    path
                },
                mstsc_id: index as u32,
                name,
                x: display.x,
                y: display.y,
                width: display.width,
                height: display.height,
                primary: display.primary,
            }
        })
        .collect()
}

#[allow(unsafe_code)]
mod win32 {
    use windows_sys::Win32::Devices::Display::{
        DisplayConfigGetDeviceInfo, GetDisplayConfigBufferSizes, QueryDisplayConfig,
        DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
        DISPLAYCONFIG_MODE_INFO, DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED,
        DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL, DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS,
        DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED, DISPLAYCONFIG_PATH_INFO,
        DISPLAYCONFIG_SOURCE_DEVICE_NAME, DISPLAYCONFIG_TARGET_DEVICE_NAME, QDC_ONLY_ACTIVE_PATHS,
    };
    use windows_sys::Win32::Graphics::Gdi::{
        EnumDisplayDevicesW, EnumDisplaySettingsW, DEVMODEW, DISPLAY_DEVICEW,
        DISPLAY_DEVICE_ATTACHED_TO_DESKTOP, DISPLAY_DEVICE_MIRRORING_DRIVER,
        DISPLAY_DEVICE_PRIMARY_DEVICE, ENUM_CURRENT_SETTINGS,
    };

    pub struct Display {
        pub gdi_name: String,
        pub x: i32,
        pub y: i32,
        pub width: u32,
        pub height: u32,
        pub primary: bool,
    }

    fn text(buffer: &[u16]) -> String {
        let length = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
        String::from_utf16_lossy(&buffer[..length])
    }

    /// Displays attached to the desktop, in EnumDisplayDevices order (the order mstsc numbers them).
    pub fn attached_displays() -> Vec<Display> {
        let mut displays = Vec::new();
        for index in 0.. {
            let mut device = DISPLAY_DEVICEW {
                cb: std::mem::size_of::<DISPLAY_DEVICEW>() as u32,
                ..Default::default()
            };
            // SAFETY: device is a writable DISPLAY_DEVICEW with cb set.
            if unsafe { EnumDisplayDevicesW(std::ptr::null(), index, &mut device, 0) } == 0 {
                break;
            }
            if device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP == 0
                || device.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER != 0
            {
                continue;
            }
            let mut mode = DEVMODEW {
                dmSize: std::mem::size_of::<DEVMODEW>() as u16,
                ..Default::default()
            };
            // SAFETY: DeviceName is null-terminated; mode is a writable DEVMODEW with dmSize set.
            if unsafe {
                EnumDisplaySettingsW(device.DeviceName.as_ptr(), ENUM_CURRENT_SETTINGS, &mut mode)
            } == 0
            {
                continue;
            }
            // SAFETY: for display devices the union holds the position fields.
            let position = unsafe { mode.Anonymous1.Anonymous2.dmPosition };
            displays.push(Display {
                gdi_name: text(&device.DeviceName),
                x: position.x,
                y: position.y,
                width: mode.dmPelsWidth,
                height: mode.dmPelsHeight,
                primary: device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE != 0,
            });
        }
        displays
    }

    /// The monitor shown by a GDI display.
    pub struct MonitorName {
        pub gdi_name: String,
        /// From the EDID; empty for most built-in panels.
        pub name: String,
        pub path: String,
        pub internal: bool,
    }

    /// The monitor of each active display path.
    pub fn monitor_names() -> Vec<MonitorName> {
        let mut path_count = 0u32;
        let mut mode_count = 0u32;
        // SAFETY: both counts are writable.
        if unsafe {
            GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &mut path_count, &mut mode_count)
        } != 0
        {
            return Vec::new();
        }
        let mut paths = vec![DISPLAYCONFIG_PATH_INFO::default(); path_count as usize];
        let mut modes = vec![DISPLAYCONFIG_MODE_INFO::default(); mode_count as usize];
        // SAFETY: the arrays have the lengths passed in the counts.
        if unsafe {
            QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS,
                &mut path_count,
                paths.as_mut_ptr(),
                &mut mode_count,
                modes.as_mut_ptr(),
                std::ptr::null_mut(),
            )
        } != 0
        {
            return Vec::new();
        }
        paths.truncate(path_count as usize);

        let mut names = Vec::new();
        for path in &paths {
            let mut source = DISPLAYCONFIG_SOURCE_DEVICE_NAME::default();
            source.header.r#type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = std::mem::size_of::<DISPLAYCONFIG_SOURCE_DEVICE_NAME>() as u32;
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;
            let mut target = DISPLAYCONFIG_TARGET_DEVICE_NAME::default();
            target.header.r#type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = std::mem::size_of::<DISPLAYCONFIG_TARGET_DEVICE_NAME>() as u32;
            target.header.adapterId = path.targetInfo.adapterId;
            target.header.id = path.targetInfo.id;
            // SAFETY: each packet starts with a header whose size matches its structure.
            let ok = unsafe {
                DisplayConfigGetDeviceInfo(&mut source.header) == 0
                    && DisplayConfigGetDeviceInfo(&mut target.header) == 0
            };
            if !ok {
                continue;
            }
            let gdi_name = text(&source.viewGdiDeviceName);
            // A duplicated display has one source and several targets; the first one names it.
            if names
                .iter()
                .any(|name: &MonitorName| name.gdi_name == gdi_name)
            {
                continue;
            }
            names.push(MonitorName {
                gdi_name,
                name: text(&target.monitorFriendlyDeviceName),
                path: text(&target.monitorDevicePath),
                internal: matches!(
                    target.outputTechnology,
                    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                        | DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS
                        | DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                        | DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED
                ),
            });
        }
        names
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn monitor(key: &str, mstsc_id: u32, primary: bool) -> Monitor {
        Monitor {
            key: key.to_string(),
            mstsc_id,
            name: key.to_string(),
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
            primary,
        }
    }

    fn selected(keys: &[&str]) -> MonitorChoice {
        MonitorChoice {
            mode: MonitorMode::Selected,
            selected: keys.iter().map(|key| key.to_string()).collect(),
        }
    }

    #[test]
    fn single_and_all_need_no_monitor_list() {
        assert_eq!(MonitorChoice::default().resolve(&[]), MonitorLayout::Single);
        assert!(MonitorLayout::Single.rdp_lines().is_empty());

        let all = MonitorChoice {
            mode: MonitorMode::All,
            selected: Vec::new(),
        };
        assert_eq!(all.resolve(&[]), MonitorLayout::All);
        assert_eq!(MonitorLayout::All.rdp_lines(), vec!["use multimon:i:1"]);
    }

    #[test]
    fn selected_monitors_become_mstsc_ids_with_the_primary_first() {
        let monitors = [
            monitor("a", 0, false),
            monitor("b", 1, false),
            monitor("c", 2, true),
        ];
        let layout = selected(&["a", "c"]).resolve(&monitors);
        assert_eq!(layout, MonitorLayout::Selected(vec![2, 0]));
        assert_eq!(
            layout.rdp_lines(),
            vec!["use multimon:i:1", "selectedmonitors:s:2,0"]
        );
    }

    #[test]
    fn missing_monitors_are_skipped_and_none_left_means_one_monitor() {
        let monitors = [monitor("a", 0, true), monitor("b", 1, false)];
        assert_eq!(
            selected(&["b", "gone"]).resolve(&monitors),
            MonitorLayout::Selected(vec![1])
        );
        assert_eq!(
            selected(&["gone"]).resolve(&monitors),
            MonitorLayout::Single
        );
    }

    const HOST: &str = "8F3B2C1A-0000-4000-8000-000000000001";
    const VM: &str = "1E6A1B5C-7D2E-4F3A-9B8C-0D1E2F3A4B5C";
    const OTHER_VM: &str = "2E6A1B5C-7D2E-4F3A-9B8C-0D1E2F3A4B5C";

    fn temp_store_path(name: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "hyperharbor-monitors-{name}-{}.json",
            std::process::id()
        ));
        let _ = std::fs::remove_file(&path);
        path
    }

    #[test]
    fn store_keeps_a_choice_per_vm_across_restarts() {
        let path = temp_store_path("per-vm");
        let store = MonitorChoiceStore::new(Some(path.clone()));
        assert_eq!(store.get(HOST, VM), MonitorChoice::default());

        store.set(HOST, VM, selected(&["a", "b"])).unwrap();
        assert_eq!(store.get(HOST, OTHER_VM), MonitorChoice::default());

        // IDs are compared without regard to case.
        let reloaded = MonitorChoiceStore::new(Some(path.clone()));
        assert_eq!(
            reloaded.get(&HOST.to_lowercase(), &VM.to_lowercase()),
            selected(&["a", "b"])
        );
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn choosing_one_monitor_removes_the_vms_entry() {
        let path = temp_store_path("single");
        let store = MonitorChoiceStore::new(Some(path.clone()));
        store.set(HOST, VM, selected(&["a"])).unwrap();
        store.set(HOST, VM, MonitorChoice::default()).unwrap();

        let text = std::fs::read_to_string(&path).unwrap();
        assert!(!text.contains(&VM.to_lowercase()), "{text}");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn store_refuses_an_empty_selection_and_an_invalid_vm_id() {
        let store = MonitorChoiceStore::new(None);
        assert!(matches!(
            store.set(HOST, VM, selected(&[])),
            Err(ClientError::InvalidRequest(_))
        ));
        assert!(matches!(
            store.set(HOST, "../x", selected(&["a"])),
            Err(ClientError::InvalidVmId)
        ));
    }

    #[test]
    fn choice_reads_camel_case_json() {
        let choice: MonitorChoice =
            serde_json::from_str(r#"{"mode":"selected","selected":["x"]}"#).unwrap();
        assert_eq!(choice, selected(&["x"]));
    }

    /// Prints this device's monitors; compare the IDs with `mstsc /l`.
    #[test]
    #[ignore]
    fn live_list_monitors() {
        for monitor in list() {
            println!("{monitor:?}");
        }
    }
}
