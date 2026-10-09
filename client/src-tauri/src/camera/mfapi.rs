//! Media Foundation's free functions, looked up when first needed instead of being imported when the
//! program loads.
//!
//! `windows-rs` imports every function it calls by name, so the loader would need `mfplat.dll`,
//! `mf.dll`, `mfreadwrite.dll`, and `mfsensorgroup.dll` (with `MFIsVirtualCameraTypeSupported` and
//! `MFCreateVirtualCamera`) before the first line of the client ran. Windows N editions and some
//! Server images have no Media Foundation, and Windows 10 has no virtual camera functions, so the
//! client would not start there. Looking the functions up here keeps camera sharing optional: where
//! they are missing, `api()` reports why and the rest of the client works as before.
//!
//! COM interfaces are called through their vtables and need no import, so only these free
//! functions are resolved by hand. `imports_no_media_foundation_at_load` checks the result.
#![allow(unsafe_code)]

use std::ffi::{c_void, CStr};
use std::sync::OnceLock;
use windows::core::{Error, Interface, BOOL, HRESULT, HSTRING, PCSTR};
use windows::Win32::Foundation::{E_POINTER, HMODULE};
use windows::Win32::Media::MediaFoundation::*;
use windows::Win32::System::Com::CoTaskMemFree;
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};

type StartupFn = unsafe extern "system" fn(u32, u32) -> HRESULT;
type CreateAttributesFn = unsafe extern "system" fn(*mut *mut c_void, u32) -> HRESULT;
type EnumDeviceSourcesFn =
    unsafe extern "system" fn(*mut c_void, *mut *mut *mut c_void, *mut u32) -> HRESULT;
type CreateReaderFn =
    unsafe extern "system" fn(*mut c_void, *mut c_void, *mut *mut c_void) -> HRESULT;
type CreateMediaTypeFn = unsafe extern "system" fn(*mut *mut c_void) -> HRESULT;
type IsSupportedFn = unsafe extern "system" fn(MFVirtualCameraType, *mut BOOL) -> HRESULT;
type CreateVirtualCameraFn = unsafe extern "system" fn(
    MFVirtualCameraType,
    MFVirtualCameraLifetime,
    MFVirtualCameraAccess,
    *const u16,
    *const u16,
    *const windows::core::GUID,
    u32,
    *mut *mut c_void,
) -> HRESULT;

pub struct Api {
    startup: StartupFn,
    create_attributes: CreateAttributesFn,
    enum_device_sources: EnumDeviceSourcesFn,
    create_reader: CreateReaderFn,
    create_media_type: CreateMediaTypeFn,
    /// Both exist only on Windows 11.
    is_supported: Option<IsSupportedFn>,
    create_virtual_camera: Option<CreateVirtualCameraFn>,
}

static API: OnceLock<Result<Api, String>> = OnceLock::new();

/// The Media Foundation functions, or why they are not available (no Media Foundation installed).
pub fn api() -> Result<&'static Api, String> {
    API.get_or_init(load).as_ref().map_err(Clone::clone)
}

fn module(name: &str) -> Result<HMODULE, String> {
    // SAFETY: loads a system library by name.
    unsafe { LoadLibraryW(&HSTRING::from(name)) }
        .map_err(|_| format!("{name} is not available; Windows has no Media Foundation here."))
}

/// # Safety
/// `T` must be the function pointer type of the export `name`.
unsafe fn export<T: Copy>(module: HMODULE, name: &CStr) -> Option<T> {
    debug_assert_eq!(std::mem::size_of::<T>(), std::mem::size_of::<usize>());
    // SAFETY: the caller guarantees the signature; a missing export gives None.
    unsafe {
        GetProcAddress(module, PCSTR(name.as_ptr().cast()))
            .map(|f| std::mem::transmute_copy::<_, T>(&f))
    }
}

fn load() -> Result<Api, String> {
    let platform = module("mfplat.dll")?;
    let mf = module("mf.dll")?;
    let reader = module("mfreadwrite.dll")?;
    // Absent before Windows 11; that is not an error here.
    let sensor_group = module("mfsensorgroup.dll").ok();
    let missing = |name: &str| format!("Media Foundation is missing {name}.");
    // SAFETY: each type alias above is the documented signature of the export it is looked up by.
    unsafe {
        Ok(Api {
            startup: export(platform, c"MFStartup").ok_or_else(|| missing("MFStartup"))?,
            create_attributes: export(platform, c"MFCreateAttributes")
                .ok_or_else(|| missing("MFCreateAttributes"))?,
            enum_device_sources: export(mf, c"MFEnumDeviceSources")
                .ok_or_else(|| missing("MFEnumDeviceSources"))?,
            create_reader: export(reader, c"MFCreateSourceReaderFromMediaSource")
                .ok_or_else(|| missing("MFCreateSourceReaderFromMediaSource"))?,
            create_media_type: export(platform, c"MFCreateMediaType")
                .ok_or_else(|| missing("MFCreateMediaType"))?,
            is_supported: sensor_group.and_then(|m| export(m, c"MFIsVirtualCameraTypeSupported")),
            create_virtual_camera: sensor_group.and_then(|m| export(m, c"MFCreateVirtualCamera")),
        })
    }
}

/// Takes ownership of one reference returned through an out pointer.
///
/// # Safety
/// `raw` is null or a pointer to a COM object of type `T` whose reference the caller owns.
unsafe fn take<T: Interface>(raw: *mut c_void) -> windows::core::Result<T> {
    if raw.is_null() {
        Err(Error::from(E_POINTER))
    } else {
        // SAFETY: guaranteed by the caller.
        Ok(unsafe { T::from_raw(raw) })
    }
}

impl Api {
    /// `MFStartup`. Media Foundation counts the calls, so it may be repeated.
    pub fn start(&self) -> windows::core::Result<()> {
        // SAFETY: plain initialization call.
        unsafe { (self.startup)(MF_VERSION, MFSTARTUP_FULL).ok() }
    }

    pub fn create_attributes(&self, size: u32) -> windows::core::Result<IMFAttributes> {
        let mut raw = std::ptr::null_mut();
        // SAFETY: `raw` is a valid out pointer; on success it holds one reference.
        unsafe {
            (self.create_attributes)(&mut raw, size).ok()?;
            take(raw)
        }
    }

    pub fn create_media_type(&self) -> windows::core::Result<IMFMediaType> {
        let mut raw = std::ptr::null_mut();
        // SAFETY: as above.
        unsafe {
            (self.create_media_type)(&mut raw).ok()?;
            take(raw)
        }
    }

    /// The activation objects of the devices matching `attributes`.
    pub fn enum_device_sources(
        &self,
        attributes: &IMFAttributes,
    ) -> windows::core::Result<Vec<IMFActivate>> {
        let mut list: *mut *mut c_void = std::ptr::null_mut();
        let mut count = 0u32;
        // SAFETY: the out pointers are valid. The array and each object in it belong to the caller:
        // the objects are taken over one by one and the array is freed once.
        unsafe {
            (self.enum_device_sources)(attributes.as_raw(), &mut list, &mut count).ok()?;
            let mut devices = Vec::with_capacity(count as usize);
            for index in 0..count as usize {
                let raw = *list.add(index);
                if let Ok(activate) = take::<IMFActivate>(raw) {
                    devices.push(activate);
                }
            }
            if !list.is_null() {
                CoTaskMemFree(Some(list as *const c_void));
            }
            Ok(devices)
        }
    }

    pub fn create_source_reader(
        &self,
        source: &IMFMediaSource,
        attributes: Option<&IMFAttributes>,
    ) -> windows::core::Result<IMFSourceReader> {
        let mut raw = std::ptr::null_mut();
        // SAFETY: the interface pointers are valid for the call; on success `raw` holds one reference.
        unsafe {
            (self.create_reader)(
                source.as_raw(),
                attributes.map_or(std::ptr::null_mut(), |a| a.as_raw()),
                &mut raw,
            )
            .ok()?;
            take(raw)
        }
    }

    /// Whether the system supports software virtual cameras (Windows 11).
    pub fn virtual_cameras_supported(&self) -> Result<bool, String> {
        let supported = self
            .is_supported
            .ok_or_else(|| "mfsensorgroup.dll has no MFIsVirtualCameraTypeSupported".to_string())?;
        let mut answer = BOOL(0);
        // SAFETY: `answer` is a valid out pointer.
        unsafe { supported(MFVirtualCameraType_SoftwareCameraSource, &mut answer) }
            .ok()
            .map_err(|e| e.message())?;
        Ok(answer.as_bool())
    }

    /// `MFCreateVirtualCamera` for a session-lifetime software camera of the current user.
    pub fn create_virtual_camera(
        &self,
        name: &str,
        source_class: &str,
    ) -> windows::core::Result<IMFVirtualCamera> {
        let create = self
            .create_virtual_camera
            .ok_or_else(|| Error::from(windows::Win32::Foundation::E_NOTIMPL))?;
        let (name, class) = (HSTRING::from(name), HSTRING::from(source_class));
        let mut raw = std::ptr::null_mut();
        // SAFETY: the strings outlive the call; on success `raw` holds one reference.
        unsafe {
            create(
                MFVirtualCameraType_SoftwareCameraSource,
                MFVirtualCameraLifetime_Session,
                MFVirtualCameraAccess_CurrentUser,
                name.as_ptr(),
                class.as_ptr(),
                std::ptr::null(),
                0,
                &mut raw,
            )
            .ok()?;
            take(raw)
        }
    }
}

#[cfg(test)]
mod tests {
    /// The names of the DLLs an executable imports, read from its PE import table.
    fn imported_dlls(image: &[u8]) -> Vec<String> {
        let u16_at = |o: usize| u16::from_le_bytes([image[o], image[o + 1]]) as usize;
        let u32_at = |o: usize| {
            u32::from_le_bytes([image[o], image[o + 1], image[o + 2], image[o + 3]]) as usize
        };
        let pe = u32_at(0x3c);
        assert_eq!(&image[pe..pe + 4], b"PE\0\0");
        let sections = u16_at(pe + 6);
        let optional = pe + 24;
        assert_eq!(u16_at(optional), 0x20b, "a 64-bit image");
        let import_rva = u32_at(optional + 112 + 8);
        let first_section = optional + u16_at(pe + 20);
        let to_offset = |rva: usize| {
            (0..sections)
                .map(|i| first_section + i * 40)
                .find_map(|s| {
                    let (virtual_size, virtual_address, raw_size, raw_pointer) = (
                        u32_at(s + 8),
                        u32_at(s + 12),
                        u32_at(s + 16),
                        u32_at(s + 20),
                    );
                    (rva >= virtual_address && rva < virtual_address + virtual_size.max(raw_size))
                        .then(|| rva - virtual_address + raw_pointer)
                })
                .expect("an RVA inside a section")
        };
        let mut names = Vec::new();
        let mut descriptor = to_offset(import_rva);
        while u32_at(descriptor + 12) != 0 {
            let start = to_offset(u32_at(descriptor + 12));
            let end = image[start..].iter().position(|&b| b == 0).unwrap() + start;
            names.push(String::from_utf8_lossy(&image[start..end]).to_ascii_lowercase());
            descriptor += 20;
        }
        names
    }

    fn own_imports() -> Vec<String> {
        imported_dlls(&std::fs::read(std::env::current_exe().unwrap()).unwrap())
    }

    #[test]
    fn the_import_reader_finds_what_this_program_really_imports() {
        let imports = own_imports();
        assert!(imports.contains(&"kernel32.dll".to_string()), "{imports:?}");
    }

    /// Media Foundation is absent on Windows N editions and some Server images, and the virtual
    /// camera functions on Windows 10. If any of these DLLs were imported at load, the client would
    /// not start there. Go through `mfapi` instead of calling the functions directly.
    #[test]
    fn imports_no_media_foundation_at_load() {
        let imports = own_imports();
        let media: Vec<&String> = imports
            .iter()
            .filter(|dll| {
                dll.starts_with("mf") || dll.starts_with("evr") || dll.starts_with("wmvcore")
            })
            .collect();
        assert!(
            media.is_empty(),
            "imported at load: {media:?} (all imports: {imports:?})"
        );
    }

    #[test]
    fn the_functions_are_found_on_this_device() {
        let api = super::api().expect("Media Foundation is installed on a development PC");
        api.start().unwrap();
        let attributes = api.create_attributes(1).unwrap();
        assert!(!windows::core::Interface::as_raw(&attributes).is_null());
        api.create_media_type().unwrap();
    }
}
