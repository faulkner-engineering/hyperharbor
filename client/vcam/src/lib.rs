//! The HyperHarbor virtual camera source: a COM DLL the Windows Frame Server loads for each
//! virtual camera the client creates with `MFCreateVirtualCamera`. It shows the frames the client
//! sends on a named pipe (see `hyperharbor-vcam-protocol`).
//!
//! Register with `regsvr32 hyperharbor_vcam.dll` (elevated; the Frame Server runs as a service and
//! reads the class from HKLM) and remove with `regsvr32 /u`. The DLL must be somewhere the
//! LocalService account can read, for example under Program Files.
#![allow(non_snake_case)]
// The exports are listed by rustc and again by the linker; the duplicate note is harmless.
#![allow(linker_messages)]

mod activator;
mod reader;
mod register;
mod source;

use activator::Activator;
use hyperharbor_vcam_protocol::SOURCE_CLSID;
use std::ffi::c_void;
use windows::core::*;
use windows::Win32::Foundation::*;
use windows::Win32::System::Com::*;

#[implement(IClassFactory)]
struct Factory;

impl IClassFactory_Impl for Factory_Impl {
    fn CreateInstance(
        &self,
        outer: Ref<IUnknown>,
        iid: *const GUID,
        out: *mut *mut c_void,
    ) -> Result<()> {
        if outer.is_some() {
            return Err(CLASS_E_NOAGGREGATION.into());
        }
        let activate: windows::Win32::Media::MediaFoundation::IMFActivate =
            Activator::new()?.into();
        // SAFETY: iid and out are the caller's arguments for this COM method.
        unsafe { activate.query(iid, out).ok() }
    }
    fn LockServer(&self, _lock: BOOL) -> Result<()> {
        Ok(())
    }
}

/// # Safety
/// Called by COM with valid pointers.
#[no_mangle]
pub unsafe extern "system" fn DllGetClassObject(
    clsid: *const GUID,
    iid: *const GUID,
    out: *mut *mut c_void,
) -> HRESULT {
    if clsid.is_null() || out.is_null() || *clsid != GUID::from_u128(SOURCE_CLSID) {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    let factory: IClassFactory = Factory.into();
    factory.query(iid, out)
}

/// The DLL stays loaded: the Frame Server keeps sources alive and unloading under them is not
/// worth the risk.
#[no_mangle]
pub extern "system" fn DllCanUnloadNow() -> HRESULT {
    S_FALSE
}

/// # Safety
/// Called by regsvr32.
#[no_mangle]
pub unsafe extern "system" fn DllRegisterServer() -> HRESULT {
    match register::register() {
        Ok(()) => S_OK,
        Err(error) => error.code(),
    }
}

/// # Safety
/// Called by regsvr32.
#[no_mangle]
pub unsafe extern "system" fn DllUnregisterServer() -> HRESULT {
    match register::unregister() {
        Ok(()) => S_OK,
        Err(error) => error.code(),
    }
}

/// # Safety
/// Called by the loader.
#[no_mangle]
pub unsafe extern "system" fn DllMain(
    module: HINSTANCE,
    reason: u32,
    _reserved: *mut c_void,
) -> BOOL {
    const DLL_PROCESS_ATTACH: u32 = 1;
    if reason == DLL_PROCESS_ATTACH {
        register::remember_module(module);
    }
    TRUE
}
