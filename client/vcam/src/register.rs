//! Registers the source's COM class under HKLM so the Frame Server (a service) can find it.

use hyperharbor_vcam_protocol::SOURCE_CLSID_STRING;
use std::sync::atomic::{AtomicIsize, Ordering};
use windows::core::*;
use windows::Win32::Foundation::*;
use windows::Win32::System::LibraryLoader::GetModuleFileNameW;
use windows::Win32::System::Registry::*;

static MODULE: AtomicIsize = AtomicIsize::new(0);

/// Called from DllMain: the handle `register` needs to find this DLL's own path.
pub fn remember_module(module: HINSTANCE) {
    MODULE.store(module.0 as isize, Ordering::SeqCst);
}

fn module_path() -> Result<String> {
    let mut buffer = [0u16; 1024];
    // SAFETY: the buffer is valid for its length; the module handle came from DllMain.
    let length = unsafe {
        GetModuleFileNameW(
            Some(HMODULE(MODULE.load(Ordering::SeqCst) as *mut _)),
            &mut buffer,
        )
    };
    if length == 0 {
        return Err(Error::from_thread());
    }
    Ok(String::from_utf16_lossy(&buffer[..length as usize]))
}

fn class_key() -> String {
    format!(r"Software\Classes\CLSID\{SOURCE_CLSID_STRING}")
}

fn set_default(key: HKEY, name: Option<&str>, value: &str) -> Result<()> {
    let wide: Vec<u16> = value.encode_utf16().chain(std::iter::once(0)).collect();
    let bytes: Vec<u8> = wide.iter().flat_map(|c| c.to_le_bytes()).collect();
    let name = name.map(HSTRING::from);
    // SAFETY: the key is open and the data is a NUL-terminated UTF-16 string.
    unsafe {
        let name_ptr = name
            .as_ref()
            .map(|n| PCWSTR(n.as_ptr()))
            .unwrap_or(PCWSTR::null());
        RegSetValueExW(key, name_ptr, None, REG_SZ, Some(&bytes)).ok()
    }
}

pub fn register() -> Result<()> {
    let path = module_path()?;
    let key_path = HSTRING::from(class_key());
    let server_path = HSTRING::from(format!(r"{}\InprocServer32", class_key()));
    // SAFETY: standard registry calls on keys opened and closed here.
    unsafe {
        let mut class = HKEY::default();
        RegCreateKeyExW(
            HKEY_LOCAL_MACHINE,
            &key_path,
            None,
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            None,
            &mut class,
            None,
        )
        .ok()?;
        let named = set_default(class, None, "HyperHarbor virtual camera source");
        let _ = RegCloseKey(class);
        named?;

        let mut server = HKEY::default();
        RegCreateKeyExW(
            HKEY_LOCAL_MACHINE,
            &server_path,
            None,
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            None,
            &mut server,
            None,
        )
        .ok()?;
        let written = set_default(server, None, &path)
            .and_then(|()| set_default(server, Some("ThreadingModel"), "Both"));
        let _ = RegCloseKey(server);
        written
    }
}

pub fn unregister() -> Result<()> {
    let key_path = HSTRING::from(class_key());
    // SAFETY: deletes this class's own key tree.
    let result = unsafe { RegDeleteTreeW(HKEY_LOCAL_MACHINE, &key_path) };
    // A class that was never registered is already unregistered.
    if result == ERROR_FILE_NOT_FOUND {
        Ok(())
    } else {
        result.ok()
    }
}
