//! Receives frames from the client over its named pipe and keeps the newest one.
//!
//! The client creates the pipe; this side connects, and only trusts it when the process serving
//! it is the one the Frame Server says created the camera. If the pipe is not there yet (or the
//! client restarts), the reader retries; the picture stays at the last frame meanwhile.

use hyperharbor_vcam_protocol::{FrameHeader, HEADER_LEN};
use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, GENERIC_READ, HANDLE};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, ReadFile, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_NONE, OPEN_EXISTING,
};
use windows::Win32::System::Pipes::GetNamedPipeServerProcessId;
use windows::Win32::System::IO::CancelIoEx;

const RETRY: Duration = Duration::from_millis(250);

/// The newest frame received.
#[derive(Clone)]
pub struct Latest {
    pub width: u32,
    pub height: u32,
    pub data: Arc<Vec<u8>>,
}

pub struct Shared {
    stop: AtomicBool,
    /// The pipe handle while connected, so `stop` can cancel a read that is waiting for a frame.
    handle: AtomicIsize,
    latest: Mutex<Option<Latest>>,
}

impl Shared {
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            stop: AtomicBool::new(false),
            handle: AtomicIsize::new(0),
            latest: Mutex::new(None),
        })
    }

    pub fn latest(&self) -> Option<Latest> {
        self.latest.lock().ok().and_then(|latest| latest.clone())
    }

    /// Ends the reader thread: the next read (or the one in progress) fails and the loop exits.
    pub fn stop(&self) {
        self.stop.store(true, Ordering::SeqCst);
        let handle = self.handle.load(Ordering::SeqCst);
        if handle != 0 {
            // SAFETY: the handle is the reader's own open pipe; cancelling a read on a handle that
            // was just closed fails harmlessly.
            unsafe {
                let _ = CancelIoEx(HANDLE(handle as *mut _), None);
            }
        }
    }
}

/// Starts the reader thread for `pipe`, whose server must be process `creator_pid`.
pub fn start(shared: Arc<Shared>, pipe: String, creator_pid: u32) {
    std::thread::spawn(move || {
        while !shared.stop.load(Ordering::SeqCst) {
            match connect(&pipe, creator_pid) {
                Some(handle) => {
                    shared.handle.store(handle.0 as isize, Ordering::SeqCst);
                    read_frames(handle, &shared);
                    shared.handle.store(0, Ordering::SeqCst);
                    // SAFETY: the handle was opened by `connect` and is closed once, here.
                    unsafe {
                        let _ = CloseHandle(handle);
                    }
                }
                None => std::thread::sleep(RETRY),
            }
        }
    });
}

fn connect(pipe: &str, creator_pid: u32) -> Option<HANDLE> {
    let wide: Vec<u16> = pipe.encode_utf16().chain(std::iter::once(0)).collect();
    // SAFETY: `wide` is a NUL-terminated string that outlives the call.
    let handle = unsafe {
        CreateFileW(
            PCWSTR(wide.as_ptr()),
            GENERIC_READ.0,
            FILE_SHARE_NONE,
            None,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            None,
        )
    }
    .ok()?;
    let mut server = 0u32;
    // SAFETY: `handle` is the pipe just opened; `server` is a valid out pointer.
    let known = unsafe { GetNamedPipeServerProcessId(handle, &mut server) }.is_ok();
    if known && server == creator_pid {
        Some(handle)
    } else {
        // Someone else serves a pipe with this name: not the client that made the camera.
        // SAFETY: closing the handle opened above.
        unsafe {
            let _ = CloseHandle(handle);
        }
        None
    }
}

fn read_frames(handle: HANDLE, shared: &Shared) {
    let mut header_bytes = [0u8; HEADER_LEN];
    while !shared.stop.load(Ordering::SeqCst) {
        if !read_exact(handle, &mut header_bytes) {
            return;
        }
        let Ok(header) = FrameHeader::decode(&header_bytes) else {
            return; // a damaged stream: reconnect
        };
        let mut data = vec![0u8; header.payload_len as usize];
        if !read_exact(handle, &mut data) {
            return;
        }
        if let Ok(mut latest) = shared.latest.lock() {
            *latest = Some(Latest {
                width: header.width,
                height: header.height,
                data: Arc::new(data),
            });
        }
    }
}

fn read_exact(handle: HANDLE, buffer: &mut [u8]) -> bool {
    let mut filled = 0;
    while filled < buffer.len() {
        let mut read = 0u32;
        // SAFETY: the slice is valid for writes of its length for the duration of the call.
        let ok = unsafe { ReadFile(handle, Some(&mut buffer[filled..]), Some(&mut read), None) };
        if ok.is_err() || read == 0 {
            return false;
        }
        filled += read as usize;
    }
    true
}
