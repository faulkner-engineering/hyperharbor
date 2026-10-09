//! The client's end of a virtual camera's frame pipe: a named pipe the camera source (inside the
//! Windows Frame Server) connects to and reads frames from. See `hyperharbor-vcam-protocol`.
//!
//! The pipe's access list lets in this user, SYSTEM, and LocalService (the account the Frame
//! Server runs as), and nobody else, so another local user cannot read the camera feed. It is
//! created as the first instance with remote clients rejected, so a process that grabbed the name
//! first makes `start` fail instead of being fed frames.
#![allow(unsafe_code)]

use super::fanout::Sink;
use super::frame::Frame;
use hyperharbor_vcam_protocol::{FrameHeader, PixelFormat, Status};
use std::io;
use std::sync::atomic::{AtomicIsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;
use windows::core::{HSTRING, PCWSTR};
use windows::Win32::Foundation::{
    CloseHandle, LocalFree, ERROR_PIPE_CONNECTED, HANDLE, HLOCAL, INVALID_HANDLE_VALUE,
};
use windows::Win32::Security::Authorization::{
    ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows::Win32::Security::{PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, WriteFile, FILE_ATTRIBUTE_NORMAL, FILE_FLAG_FIRST_PIPE_INSTANCE,
    FILE_GENERIC_READ, FILE_SHARE_NONE, OPEN_EXISTING, PIPE_ACCESS_OUTBOUND,
};
use windows::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_REJECT_REMOTE_CLIENTS,
    PIPE_TYPE_BYTE, PIPE_WAIT,
};
use windows::Win32::System::IO::CancelIoEx;

/// Owner rights (this user) and SYSTEM get everything; LocalService may read. Protected: no
/// inherited entries.
const PIPE_SDDL: &str = "D:P(A;;GA;;;SY)(A;;GA;;;OW)(A;;GR;;;LS)";

/// Two 720p frames of buffer, so one slow read does not stall the writer for long.
const BUFFER_BYTES: u32 = 3 * 1024 * 1024;

#[derive(Default)]
struct SlotState {
    next: Option<(Status, Frame)>,
    closed: bool,
}

struct Slot {
    state: Mutex<SlotState>,
    wake: Condvar,
    /// The pipe handle, for cancelling a blocked write when the sink is dropped.
    handle: AtomicIsize,
}

/// Serves frames to one virtual camera's source. Dropping it closes the pipe.
pub struct PipeSink {
    name: String,
    slot: Arc<Slot>,
    thread: Option<JoinHandle<()>>,
}

impl PipeSink {
    /// Creates the pipe `name` and starts serving. Fails if the name is already taken.
    pub fn start(name: &str) -> io::Result<Self> {
        let handle = create_pipe(name)?;
        let slot = Arc::new(Slot {
            state: Mutex::new(SlotState::default()),
            wake: Condvar::new(),
            handle: AtomicIsize::new(handle.0 as isize),
        });
        let serving = slot.clone();
        // A HANDLE is a plain pointer-sized value but not `Send`; the serving thread alone owns it.
        let raw = handle.0 as isize;
        let thread = std::thread::spawn(move || serve(HANDLE(raw as *mut _), &serving));
        Ok(Self {
            name: name.to_string(),
            slot,
            thread: Some(thread),
        })
    }
}

impl Sink for PipeSink {
    fn send(&mut self, status: Status, frame: &Frame) {
        if let Ok(mut state) = self.slot.state.lock() {
            state.next = Some((status, frame.clone()));
            self.slot.wake.notify_one();
        }
    }
}

impl Drop for PipeSink {
    fn drop(&mut self) {
        if let Ok(mut state) = self.slot.state.lock() {
            state.closed = true;
            self.slot.wake.notify_all();
        }
        // The serving thread may be waiting for a client to connect or blocked writing to one:
        // cancel the write, and connect to our own pipe to end the wait.
        let handle = self.slot.handle.load(Ordering::SeqCst);
        // SAFETY: cancelling I/O on a pipe handle owned by the serving thread (still open until
        // that thread ends) and opening then closing a client handle to wake the wait.
        unsafe {
            if handle != 0 {
                let _ = CancelIoEx(HANDLE(handle as *mut _), None);
            }
            let wide = HSTRING::from(self.name.as_str());
            if let Ok(client) = CreateFileW(
                PCWSTR(wide.as_ptr()),
                FILE_GENERIC_READ.0,
                FILE_SHARE_NONE,
                None,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                None,
            ) {
                let _ = CloseHandle(client);
            }
        }
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

fn create_pipe(name: &str) -> io::Result<HANDLE> {
    let sddl = HSTRING::from(PIPE_SDDL);
    let mut descriptor = PSECURITY_DESCRIPTOR::default();
    // SAFETY: converts a constant SDDL string; the descriptor is freed below after the pipe is
    // created (the pipe keeps its own copy).
    unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            PCWSTR(sddl.as_ptr()),
            SDDL_REVISION_1,
            &mut descriptor,
            None,
        )
        .map_err(io::Error::other)?;
        let attributes = SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: descriptor.0,
            bInheritHandle: false.into(),
        };
        let wide = HSTRING::from(name);
        let handle = CreateNamedPipeW(
            PCWSTR(wide.as_ptr()),
            PIPE_ACCESS_OUTBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1,
            BUFFER_BYTES,
            0,
            0,
            Some(&attributes),
        );
        let _ = LocalFree(Some(HLOCAL(descriptor.0)));
        if handle == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }
        Ok(handle)
    }
}

fn serve(handle: HANDLE, slot: &Slot) {
    loop {
        if closed(slot) {
            break;
        }
        // SAFETY: waits for a client on the pipe this thread owns. A client that connected before
        // this call reports ERROR_PIPE_CONNECTED, which is success.
        let connected = unsafe { ConnectNamedPipe(handle, None) };
        let ready = match connected {
            Ok(()) => true,
            Err(error) => error.code() == ERROR_PIPE_CONNECTED.to_hresult(),
        };
        if closed(slot) {
            break;
        }
        if ready {
            write_frames(handle, slot);
        }
        // SAFETY: ends the connection so the next client can connect.
        unsafe {
            let _ = DisconnectNamedPipe(handle);
        }
    }
    slot.handle.store(0, Ordering::SeqCst);
    // SAFETY: closes the handle created in `start`, once.
    unsafe {
        let _ = CloseHandle(handle);
    }
}

fn closed(slot: &Slot) -> bool {
    slot.state.lock().map(|state| state.closed).unwrap_or(true)
}

/// Sends frames as they arrive until the client goes away or the sink closes.
fn write_frames(handle: HANDLE, slot: &Slot) {
    loop {
        let next = {
            let Ok(mut state) = slot.state.lock() else {
                return;
            };
            while state.next.is_none() && !state.closed {
                match slot.wake.wait_timeout(state, Duration::from_millis(500)) {
                    Ok((guard, _)) => state = guard,
                    Err(_) => return,
                }
            }
            if state.closed {
                return;
            }
            state.next.take()
        };
        let Some((status, frame)) = next else {
            continue;
        };
        let header = FrameHeader {
            width: frame.size.width,
            height: frame.size.height,
            format: PixelFormat::Nv12,
            status,
            sequence: frame.sequence,
            payload_len: frame.data.len() as u32,
        };
        if !write_all(handle, &header.encode()) || !write_all(handle, &frame.data) {
            return;
        }
    }
}

fn write_all(handle: HANDLE, mut bytes: &[u8]) -> bool {
    while !bytes.is_empty() {
        let mut written = 0u32;
        // SAFETY: the slice is valid for reads of its length for the duration of the call.
        let ok = unsafe { WriteFile(handle, Some(bytes), Some(&mut written), None) };
        if ok.is_err() || written == 0 {
            return false;
        }
        bytes = &bytes[written as usize..];
    }
    true
}

#[cfg(test)]
mod tests {
    use super::super::frame::FrameSize;
    use super::*;
    use hyperharbor_vcam_protocol::{pipe_name, HEADER_LEN};
    use std::io::Read;

    fn frame(sequence: u64, luma: u8) -> Frame {
        let size = FrameSize::new(16, 8);
        Frame::new(size, vec![luma; size.nv12_len()], sequence).unwrap()
    }

    fn unique_pipe(tag: &str) -> String {
        pipe_name(
            std::process::id(),
            &format!("test-{tag}-{:?}", std::time::Instant::now()),
        )
    }

    fn open_client(name: &str) -> std::fs::File {
        for _ in 0..100 {
            if let Ok(file) = std::fs::OpenOptions::new().read(true).open(name) {
                return file;
            }
            std::thread::sleep(Duration::from_millis(20));
        }
        panic!("the pipe never became available");
    }

    #[test]
    fn a_connected_reader_receives_a_frame_with_a_valid_header() {
        let name = unique_pipe("frame");
        let mut sink = PipeSink::start(&name).unwrap();
        let mut client = open_client(&name);
        sink.send(Status::Live, &frame(5, 77));

        let mut header = [0u8; HEADER_LEN];
        client.read_exact(&mut header).unwrap();
        let header = FrameHeader::decode(&header).unwrap();
        assert_eq!((header.width, header.height, header.sequence), (16, 8, 5));
        assert_eq!(header.status, Status::Live);
        let mut payload = vec![0u8; header.payload_len as usize];
        client.read_exact(&mut payload).unwrap();
        assert!(payload.iter().all(|&b| b == 77));
    }

    #[test]
    fn only_the_newest_pending_frame_is_sent() {
        let name = unique_pipe("newest");
        let mut sink = PipeSink::start(&name).unwrap();
        // Two frames are queued before anyone connects; the slot keeps the newest.
        sink.send(Status::Live, &frame(1, 10));
        sink.send(Status::Shuttered, &frame(2, 20));
        let mut client = open_client(&name);
        let mut header = [0u8; HEADER_LEN];
        client.read_exact(&mut header).unwrap();
        let header = FrameHeader::decode(&header).unwrap();
        assert_eq!((header.sequence, header.status), (2, Status::Shuttered));
    }

    #[test]
    fn a_second_server_cannot_take_the_same_pipe_name() {
        let name = unique_pipe("taken");
        let _first = PipeSink::start(&name).unwrap();
        assert!(PipeSink::start(&name).is_err());
    }

    #[test]
    fn a_new_reader_can_connect_after_the_first_goes_away() {
        let name = unique_pipe("again");
        let mut sink = PipeSink::start(&name).unwrap();
        {
            let mut first = open_client(&name);
            sink.send(Status::Live, &frame(1, 1));
            let mut header = [0u8; HEADER_LEN];
            first.read_exact(&mut header).unwrap();
        }
        // The first reader is gone; a write fails, the pipe resets, and the next reader connects.
        sink.send(Status::Live, &frame(2, 2));
        std::thread::sleep(Duration::from_millis(200));
        let mut second = open_client(&name);
        sink.send(Status::Live, &frame(3, 3));
        let mut header = [0u8; HEADER_LEN];
        second.read_exact(&mut header).unwrap();
        assert!(FrameHeader::decode(&header).unwrap().sequence >= 2);
    }

    #[test]
    fn dropping_the_sink_ends_the_serving_thread_even_with_no_reader() {
        let name = unique_pipe("drop");
        let sink = PipeSink::start(&name).unwrap();
        let started = std::time::Instant::now();
        drop(sink);
        assert!(started.elapsed() < Duration::from_secs(5));
        // The name is free again.
        let _again = PipeSink::start(&name).unwrap();
    }

    #[test]
    fn dropping_the_sink_ends_the_serving_thread_while_a_reader_is_not_reading() {
        let name = unique_pipe("drop-busy");
        let mut sink = PipeSink::start(&name).unwrap();
        let _client = open_client(&name);
        // Fill the pipe buffer so the writer blocks, then drop.
        for n in 0..8 {
            sink.send(Status::Live, &frame(n, 9));
            std::thread::sleep(Duration::from_millis(20));
        }
        let started = std::time::Instant::now();
        drop(sink);
        assert!(started.elapsed() < Duration::from_secs(5));
    }
}
