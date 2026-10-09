//! The class the Frame Server creates for a virtual camera. It is an `IMFActivate`: the Frame Server
//! sets attributes on it (the camera's friendly name and the process that created it, observed in
//! testing), then asks it to activate the media source. Those two attributes pick the pipe the
//! source reads frames from, so one registered class serves any number of cameras.

use crate::source::Source;
use hyperharbor_vcam_protocol::pipe_name;
use std::ffi::c_void;
use windows::core::*;
use windows::Win32::Foundation::*;
use windows::Win32::Media::MediaFoundation::*;
use windows::Win32::System::Com::CoTaskMemFree;
use windows::Win32::System::Com::StructuredStorage::PROPVARIANT;

/// The attribute the Frame Server uses for the id of the process that created the camera.
const CREATOR_PROCESS_ID: GUID = GUID::from_u128(0x5f8d322e_0fe4_43e4_9e50_d83ecd9fc2b8);

#[implement(IMFActivate)]
pub struct Activator {
    inner: IMFAttributes,
}

impl Activator {
    pub fn new() -> Result<Self> {
        let mut attributes = None;
        // SAFETY: creates an attribute store owned by this object.
        unsafe { MFCreateAttributes(&mut attributes, 8)? };
        Ok(Self {
            inner: attributes.ok_or_else(|| Error::from(E_FAIL))?,
        })
    }

    fn friendly_name(&self) -> String {
        let mut text = PWSTR::null();
        let mut length = 0u32;
        // SAFETY: the out pointers are valid; the allocated string is freed once after copying.
        unsafe {
            if self
                .inner
                .GetAllocatedString(
                    &MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME,
                    &mut text,
                    &mut length,
                )
                .is_err()
            {
                return String::new();
            }
            let name = text.to_string().unwrap_or_default();
            CoTaskMemFree(Some(text.0 as *const c_void));
            name
        }
    }

    /// The creating process. Missing when the class is activated for a check in the creator's own
    /// process, which is then the process creating it.
    fn creator_pid(&self) -> u32 {
        // SAFETY: a plain attribute read.
        unsafe { self.inner.GetUINT32(&CREATOR_PROCESS_ID) }.unwrap_or_else(|_| std::process::id())
    }
}

impl IMFActivate_Impl for Activator_Impl {
    fn ActivateObject(&self, riid: *const GUID, ppv: *mut *mut c_void) -> Result<()> {
        let pid = self.creator_pid();
        let source: IMFMediaSourceEx =
            Source::new(pipe_name(pid, &self.friendly_name()), pid)?.into();
        // SAFETY: riid and ppv are the caller's arguments for this COM method.
        unsafe { source.query(riid, ppv).ok() }
    }
    fn ShutdownObject(&self) -> Result<()> {
        Ok(())
    }
    fn DetachObject(&self) -> Result<()> {
        Ok(())
    }
}

// The rest forwards the attribute store to `inner`.
impl IMFAttributes_Impl for Activator_Impl {
    fn GetItem(&self, key: *const GUID, value: *mut PROPVARIANT) -> Result<()> {
        // SAFETY: pointers come from the COM caller.
        unsafe { self.inner.GetItem(key, Some(value)) }
    }
    fn GetItemType(&self, key: *const GUID) -> Result<MF_ATTRIBUTE_TYPE> {
        // SAFETY: as above.
        unsafe { self.inner.GetItemType(key) }
    }
    fn CompareItem(&self, key: *const GUID, value: *const PROPVARIANT) -> Result<BOOL> {
        // SAFETY: as above.
        unsafe { self.inner.CompareItem(key, value) }
    }
    fn Compare(&self, theirs: Ref<IMFAttributes>, kind: MF_ATTRIBUTES_MATCH_TYPE) -> Result<BOOL> {
        // SAFETY: as above.
        unsafe { self.inner.Compare(theirs.as_ref(), kind) }
    }
    fn GetUINT32(&self, key: *const GUID) -> Result<u32> {
        // SAFETY: as above.
        unsafe { self.inner.GetUINT32(key) }
    }
    fn GetUINT64(&self, key: *const GUID) -> Result<u64> {
        // SAFETY: as above.
        unsafe { self.inner.GetUINT64(key) }
    }
    fn GetDouble(&self, key: *const GUID) -> Result<f64> {
        // SAFETY: as above.
        unsafe { self.inner.GetDouble(key) }
    }
    fn GetGUID(&self, key: *const GUID) -> Result<GUID> {
        // SAFETY: as above.
        unsafe { self.inner.GetGUID(key) }
    }
    fn GetStringLength(&self, key: *const GUID) -> Result<u32> {
        // SAFETY: as above.
        unsafe { self.inner.GetStringLength(key) }
    }
    fn GetString(
        &self,
        key: *const GUID,
        buffer: PWSTR,
        size: u32,
        length: *mut u32,
    ) -> Result<()> {
        // SAFETY: the buffer is valid for `size` characters, as the COM contract requires.
        unsafe {
            self.inner.GetString(
                key,
                std::slice::from_raw_parts_mut(buffer.0, size as usize),
                Some(length),
            )
        }
    }
    fn GetAllocatedString(
        &self,
        key: *const GUID,
        text: *mut PWSTR,
        length: *mut u32,
    ) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.GetAllocatedString(key, text, length) }
    }
    fn GetBlobSize(&self, key: *const GUID) -> Result<u32> {
        // SAFETY: as above.
        unsafe { self.inner.GetBlobSize(key) }
    }
    fn GetBlob(
        &self,
        key: *const GUID,
        buffer: *mut u8,
        size: u32,
        written: *mut u32,
    ) -> Result<()> {
        // SAFETY: the buffer is valid for `size` bytes, as the COM contract requires.
        unsafe {
            self.inner.GetBlob(
                key,
                std::slice::from_raw_parts_mut(buffer, size as usize),
                Some(written),
            )
        }
    }
    fn GetAllocatedBlob(
        &self,
        key: *const GUID,
        buffer: *mut *mut u8,
        size: *mut u32,
    ) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.GetAllocatedBlob(key, buffer, size) }
    }
    fn GetUnknown(&self, key: *const GUID, iid: *const GUID, out: *mut *mut c_void) -> Result<()> {
        // SAFETY: as above.
        unsafe {
            self.inner
                .GetUnknown::<IUnknown>(key)
                .and_then(|u| u.query(iid, out).ok())
        }
    }
    fn SetItem(&self, key: *const GUID, value: *const PROPVARIANT) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.SetItem(key, value) }
    }
    fn DeleteItem(&self, key: *const GUID) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.DeleteItem(key) }
    }
    fn DeleteAllItems(&self) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.DeleteAllItems() }
    }
    fn SetUINT32(&self, key: *const GUID, value: u32) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.SetUINT32(key, value) }
    }
    fn SetUINT64(&self, key: *const GUID, value: u64) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.SetUINT64(key, value) }
    }
    fn SetDouble(&self, key: *const GUID, value: f64) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.SetDouble(key, value) }
    }
    fn SetGUID(&self, key: *const GUID, value: *const GUID) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.SetGUID(key, value) }
    }
    fn SetString(&self, key: *const GUID, value: &PCWSTR) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.SetString(key, *value) }
    }
    fn SetBlob(&self, key: *const GUID, buffer: *const u8, size: u32) -> Result<()> {
        // SAFETY: the buffer is valid for `size` bytes, as the COM contract requires.
        unsafe {
            self.inner
                .SetBlob(key, std::slice::from_raw_parts(buffer, size as usize))
        }
    }
    fn SetUnknown(&self, key: *const GUID, value: Ref<IUnknown>) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.SetUnknown(key, value.as_ref()) }
    }
    fn LockStore(&self) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.LockStore() }
    }
    fn UnlockStore(&self) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.UnlockStore() }
    }
    fn GetCount(&self) -> Result<u32> {
        // SAFETY: as above.
        unsafe { self.inner.GetCount() }
    }
    fn GetItemByIndex(&self, index: u32, key: *mut GUID, value: *mut PROPVARIANT) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.GetItemByIndex(index, key, Some(value)) }
    }
    fn CopyAllItems(&self, destination: Ref<IMFAttributes>) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.inner.CopyAllItems(destination.as_ref()) }
    }
}
