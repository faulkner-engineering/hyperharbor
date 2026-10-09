//! The Windows implementation of the camera service's `Backend`.
#![allow(unsafe_code)]

use super::capture::{CaptureEvent, PhysicalCamera};
use super::fanout::Sink;
use super::ledger::Ledger;
use super::pipe::PipeSink;
use super::reconcile::{reconcile, ReconcileReport};
use super::resolve::{DeviceEnumerator, VideoDevice};
use super::service::{Backend, Held};
use super::support::Support;
use super::winmf::{self, MfDeviceEnumerator, VirtualCamera, WindowsProcessProbe, WindowsRegistry};
use super::CameraError;
use hyperharbor_vcam_protocol::pipe_name;
use std::sync::mpsc::Sender;
use windows_sys::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};

pub struct WindowsBackend;

impl Backend for WindowsBackend {
    fn support(&self) -> Support {
        winmf::detect_support()
    }

    fn create_camera(&self, name: &str) -> Result<Held, CameraError> {
        Ok(Box::new(VirtualCamera::create(name)?))
    }

    fn start_pipe(&self, name: &str) -> Result<Box<dyn Sink>, CameraError> {
        PipeSink::start(&pipe_name(std::process::id(), name))
            .map(|sink| Box::new(sink) as Box<dyn Sink>)
            .map_err(|e| {
                CameraError::Enumeration(format!("The frame pipe could not be created: {e}"))
            })
    }

    fn devices(&self) -> Result<Vec<VideoDevice>, CameraError> {
        MfDeviceEnumerator.video_devices()
    }

    fn start_capture(&self, events: Sender<CaptureEvent>) -> Held {
        Box::new(PhysicalCamera::start(None, events))
    }

    fn foreground_pid(&self) -> Option<u32> {
        // SAFETY: both calls only read window state; a null window or a failed lookup gives None.
        unsafe {
            let window = GetForegroundWindow();
            if window.is_null() {
                return None;
            }
            let mut pid = 0u32;
            GetWindowThreadProcessId(window, &mut pid);
            (pid != 0).then_some(pid)
        }
    }

    fn own_pid(&self) -> u32 {
        std::process::id()
    }

    fn source_installed(&self) -> bool {
        super::setup_windows::source_installed()
    }

    fn reconcile(&self, ledger: &mut Ledger) -> Result<ReconcileReport, CameraError> {
        winmf::start_media_foundation()?;
        reconcile(
            &WindowsRegistry,
            ledger,
            &WindowsProcessProbe,
            std::process::id(),
        )
    }
}
