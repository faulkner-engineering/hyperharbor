//! The Windows side of camera sharing: Media Foundation virtual cameras, device listing, and a
//! process check. Free Media Foundation functions are reached through `mfapi`, which looks them up
//! at run time so the client still starts on Windows 10 and on editions without Media Foundation.
#![allow(unsafe_code)]

use super::mfapi;
use super::reconcile::{ProcessProbe, VirtualCameraRegistry};
use super::resolve::{DeviceEnumerator, VideoDevice};
use super::support::Support;
use super::CameraError;
use hyperharbor_vcam_protocol::SOURCE_CLSID_STRING;
use std::ffi::c_void;
use windows::core::{HSTRING, PCSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, STILL_ACTIVE};
use windows::Win32::Media::MediaFoundation::*;
use windows::Win32::System::Com::{CoInitializeEx, CoTaskMemFree, COINIT_MULTITHREADED};
use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
use windows::Win32::System::SystemInformation::OSVERSIONINFOW;
use windows::Win32::System::Threading::{
    GetExitCodeProcess, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
};

pub(super) fn mf_error(error: windows::core::Error) -> String {
    format!(
        "{} (0x{:08X})",
        error.message().trim(),
        error.code().0 as u32
    )
}

/// Starts COM (multithreaded) and Media Foundation. Safe to call repeatedly; Media Foundation
/// counts the calls. Fails, without affecting the rest of the client, where Media Foundation is
/// not installed.
pub fn start_media_foundation() -> Result<(), CameraError> {
    let api = mfapi::api().map_err(CameraError::Enumeration)?;
    // SAFETY: plain initialization. A thread already in another COM mode is fine for the calls
    // made here, so that result is ignored.
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    }
    api.start()
        .map_err(|e| CameraError::Enumeration(mf_error(e)))
}

/// The Windows build number (22000 or more is Windows 11). Read with `RtlGetVersion`: the
/// documented `GetVersionExW` reports Windows 8 (build 9200) to a program without a compatibility
/// manifest, which would switch camera sharing off on every Windows 11 device (found in testing).
pub fn windows_build() -> u32 {
    type RtlGetVersionFn = unsafe extern "system" fn(*mut OSVERSIONINFOW) -> i32;
    let mut info = OSVERSIONINFOW {
        dwOSVersionInfoSize: std::mem::size_of::<OSVERSIONINFOW>() as u32,
        ..Default::default()
    };
    // SAFETY: ntdll is loaded in every process; the export has the documented signature and
    // `info` is a properly sized, writable OSVERSIONINFOW.
    unsafe {
        let Ok(ntdll) = GetModuleHandleW(&HSTRING::from("ntdll.dll")) else {
            return 0;
        };
        let Some(address) = GetProcAddress(ntdll, PCSTR(c"RtlGetVersion".as_ptr().cast())) else {
            return 0;
        };
        let function =
            std::mem::transmute::<unsafe extern "system" fn() -> isize, RtlGetVersionFn>(address);
        if function(&mut info) == 0 {
            info.dwBuildNumber
        } else {
            0
        }
    }
}

/// Whether this device can create virtual cameras, from the build number and the API itself.
pub fn detect_support() -> Support {
    let build = windows_build();
    // The probe loads libraries, so it runs only on builds that could have the API.
    if build < super::support::MIN_BUILD {
        return Support::from_probe(build, Ok(false));
    }
    let probe = mfapi::api().and_then(|api| api.virtual_cameras_supported());
    Support::from_probe(build, probe)
}

/// One virtual camera. The camera has session lifetime: Windows removes it when this process
/// exits, and dropping this removes it at once.
pub struct VirtualCamera {
    camera: IMFVirtualCamera,
}

// SAFETY: the camera object is created on a thread in the multithreaded COM apartment (see
// `start_media_foundation`), so it may be called, and released, from any thread. `windows-rs`
// does not mark COM interfaces `Send` because it cannot know that.
unsafe impl Send for VirtualCamera {}

impl VirtualCamera {
    /// Creates and starts a virtual camera named `name`, backed by the HyperHarbor source class.
    pub fn create(name: &str) -> Result<Self, CameraError> {
        start_media_foundation()?;
        let api = mfapi::api().map_err(CameraError::Enumeration)?;
        let camera = api
            .create_virtual_camera(name, SOURCE_CLSID_STRING)
            .map_err(|e| CameraError::Enumeration(mf_error(e)))?;
        // SAFETY: starts the camera just created.
        unsafe { camera.Start(None) }.map_err(|e| CameraError::Enumeration(mf_error(e)))?;
        Ok(Self { camera })
    }
}

impl Drop for VirtualCamera {
    fn drop(&mut self) {
        // The dropping thread may never have used COM; joining the multithreaded apartment first
        // lets it call the camera.
        let _ = start_media_foundation();
        // SAFETY: removes and shuts down the camera this value created; failures leave nothing to
        // undo (the session-lifetime camera goes when the process does).
        unsafe {
            let _ = self.camera.Remove();
            let _ = self.camera.Shutdown();
        }
    }
}

/// Lists the video capture devices through Media Foundation.
pub struct MfDeviceEnumerator;

/// The video capture devices as (activation object, name, symbolic link). Devices without both a
/// name and a link are left out.
pub(super) fn video_capture_activations() -> Result<Vec<(IMFActivate, VideoDevice)>, CameraError> {
    start_media_foundation()?;
    let api = mfapi::api().map_err(CameraError::Enumeration)?;
    let fail = |e: windows::core::Error| CameraError::Enumeration(mf_error(e));
    let attributes = api.create_attributes(1).map_err(fail)?;
    // SAFETY: a plain attribute call on the store just created.
    unsafe {
        attributes
            .SetGUID(
                &MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                &MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID,
            )
            .map_err(fail)?;
    }
    let mut devices = Vec::new();
    for activate in api.enum_device_sources(&attributes).map_err(fail)? {
        // SAFETY: `activate` is a valid activation object.
        let (name, link) = unsafe {
            (
                read_string(&activate, &MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME),
                read_string(
                    &activate,
                    &MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
                ),
            )
        };
        if let (Some(name), Some(symbolic_link)) = (name, link) {
            devices.push((
                activate,
                VideoDevice {
                    name,
                    symbolic_link,
                },
            ));
        }
    }
    Ok(devices)
}

impl DeviceEnumerator for MfDeviceEnumerator {
    fn video_devices(&self) -> Result<Vec<VideoDevice>, CameraError> {
        Ok(video_capture_activations()?
            .into_iter()
            .map(|(_, device)| device)
            .collect())
    }
}

/// # Safety
/// `activate` must be a valid activation object.
pub(super) unsafe fn read_string(
    activate: &IMFActivate,
    key: &windows::core::GUID,
) -> Option<String> {
    let mut text = PWSTR::null();
    let mut length = 0u32;
    // SAFETY: the out pointers are valid; the allocated string is freed once after copying.
    unsafe {
        activate
            .GetAllocatedString(key, &mut text, &mut length)
            .ok()?;
        let value = text.to_string().ok();
        CoTaskMemFree(Some(text.0 as *const c_void));
        value
    }
}

/// Lists cameras and removes orphans through the Windows API.
pub struct WindowsRegistry;

impl VirtualCameraRegistry for WindowsRegistry {
    fn list(&self) -> Result<Vec<VideoDevice>, CameraError> {
        MfDeviceEnumerator.video_devices()
    }

    fn remove(&self, created_name: &str) -> Result<(), CameraError> {
        // Session-lifetime cameras vanish with their creator, so an orphan that is still listed is
        // rare (a system-lifetime camera from another build, or a creator that is still shutting
        // down). Creating the camera again under the same name and removing it removes the device.
        let camera = VirtualCamera::create(created_name)?;
        // SAFETY: removes the camera just created.
        unsafe { camera.camera.Remove() }.map_err(|e| CameraError::Removal(mf_error(e)))
    }
}

/// Process liveness through `OpenProcess`.
pub struct WindowsProcessProbe;

impl ProcessProbe for WindowsProcessProbe {
    fn is_alive(&self, pid: u32) -> bool {
        // SAFETY: opens a process for a limited query and closes the handle.
        unsafe {
            let Ok(handle) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) else {
                // No such process (or no access to it, which for a process of this user means
                // it is gone).
                return false;
            };
            let mut code = 0u32;
            let alive =
                GetExitCodeProcess(handle, &mut code).is_ok() && code == STILL_ACTIVE.0 as u32;
            let _ = CloseHandle(handle);
            alive
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A test binary has no compatibility manifest, which is exactly the case where
    /// `GetVersionExW` reports Windows 8 (9200). The real number is at least Windows 10's first build.
    #[test]
    fn the_build_number_is_the_real_one_not_the_compatibility_shim() {
        let build = windows_build();
        assert!(build >= 10240, "got {build}");
        assert_ne!(build, 9200);
    }

    #[test]
    fn this_device_is_classified_consistently_with_its_build() {
        let support = detect_support();
        if windows_build() < super::super::support::MIN_BUILD {
            assert!(matches!(support, Support::NeedsWindows11 { .. }));
        } else {
            assert!(!matches!(support, Support::NeedsWindows11 { .. }));
        }
    }
}
