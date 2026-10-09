//! Opens the physical camera once and reads NV12 frames from it.
//!
//! The camera is opened through Media Foundation's source reader with video processing on, so a
//! camera that only offers MJPEG or YUY2 is converted to NV12. 1280x720 at 30 fps is requested
//! first, then 640x480. When the camera cannot be opened or stops, the failure is classified (in
//! use by another application, unplugged, blocked by Windows' privacy setting) and reported, and
//! opening is retried: the other application may close.
#![allow(unsafe_code)]

use super::failure::{classify, CameraFailure};
use super::frame::{Frame, FrameSize};
use super::naming;
use super::resolve::VideoDevice;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Sender;
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::Duration;
use windows::Win32::Media::MediaFoundation::*;

/// Sizes requested, preferred first.
pub const REQUESTED_SIZES: [FrameSize; 2] = [FrameSize::HD, FrameSize::VGA];

/// How long to wait before opening the camera again after a failure the user can fix by closing
/// something (an application using it) or reconnecting.
const RETRY_TRANSIENT: Duration = Duration::from_secs(3);
/// Longer for a privacy block or a missing camera, which will not fix themselves in seconds.
const RETRY_PERSISTENT: Duration = Duration::from_secs(15);

/// What the capture thread reports.
#[derive(Debug)]
pub enum CaptureEvent {
    /// The camera opened; frames follow.
    Started {
        size: FrameSize,
        device: String,
    },
    Frame(Frame),
    /// The camera could not be used; the thread retries.
    Failed {
        failure: CameraFailure,
        detail: String,
    },
}

/// The device to capture from: the preferred one if it is listed, else the first physical camera.
/// Virtual cameras (this client's and others', recognised by their device path) are never chosen,
/// or the client would capture its own output.
pub fn choose_physical<'a>(
    devices: &'a [VideoDevice],
    preferred_link: Option<&str>,
) -> Option<&'a VideoDevice> {
    let physical = |device: &&VideoDevice| {
        naming::owned_label(&device.name).is_none() && !is_virtual_link(&device.symbolic_link)
    };
    preferred_link
        .and_then(|link| {
            devices
                .iter()
                .filter(physical)
                .find(|device| device.symbolic_link.eq_ignore_ascii_case(link))
        })
        .or_else(|| devices.iter().find(physical))
}

/// Software cameras created through `MFCreateVirtualCamera` (and other software devices) live in
/// the `SWD` enumerator.
pub fn is_virtual_link(link: &str) -> bool {
    link.to_ascii_lowercase().starts_with(r"\\?\swd#")
}

/// Copies an NV12 frame whose rows are `stride` bytes apart into a tightly packed frame. Cameras
/// are allowed to pad rows; the virtual camera expects none. None when `src` is too short.
pub fn repack_nv12(src: &[u8], size: FrameSize, stride: usize) -> Option<Vec<u8>> {
    let (width, height) = (size.width as usize, size.height as usize);
    if stride < width || !size.is_valid() {
        return None;
    }
    if stride == width {
        return src.get(..size.nv12_len()).map(<[u8]>::to_vec);
    }
    // Y: `height` rows of `stride`; UV: `height / 2` rows of `stride`, each holding `width` bytes.
    if src.len() < stride * height * 3 / 2 {
        return None;
    }
    let mut out = Vec::with_capacity(size.nv12_len());
    for row in 0..height {
        out.extend_from_slice(&src[row * stride..row * stride + width]);
    }
    let chroma = &src[stride * height..];
    for row in 0..height / 2 {
        out.extend_from_slice(&chroma[row * stride..row * stride + width]);
    }
    Some(out)
}

/// A running capture thread. Dropping it stops the thread and releases the camera.
pub struct PhysicalCamera {
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl PhysicalCamera {
    /// Starts capturing and sends events to `events` until dropped.
    pub fn start(preferred_link: Option<String>, events: Sender<CaptureEvent>) -> Self {
        let stop = Arc::new(AtomicBool::new(false));
        let flag = stop.clone();
        let thread = std::thread::spawn(move || run(preferred_link.as_deref(), &events, &flag));
        Self {
            stop,
            thread: Some(thread),
        }
    }
}

impl Drop for PhysicalCamera {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

fn run(preferred: Option<&str>, events: &Sender<CaptureEvent>, stop: &AtomicBool) {
    if super::winmf::start_media_foundation().is_err() {
        let _ = events.send(CaptureEvent::Failed {
            failure: CameraFailure::Other,
            detail: "Media Foundation did not start.".into(),
        });
        return;
    }
    let mut sequence = 0u64;
    while !stop.load(Ordering::SeqCst) {
        let outcome = capture_once(preferred, events, stop, &mut sequence);
        if stop.load(Ordering::SeqCst) {
            return;
        }
        let (failure, detail) = match outcome {
            Ok(()) => (
                CameraFailure::Gone,
                "The camera stopped sending frames.".to_string(),
            ),
            Err(failed) => failed,
        };
        if events
            .send(CaptureEvent::Failed { failure, detail })
            .is_err()
        {
            return; // nobody is listening any more
        }
        let wait = if failure.is_transient() {
            RETRY_TRANSIENT
        } else {
            RETRY_PERSISTENT
        };
        let until = std::time::Instant::now() + wait;
        while std::time::Instant::now() < until && !stop.load(Ordering::SeqCst) {
            std::thread::sleep(Duration::from_millis(100));
        }
    }
}

fn hresult_failure(error: &windows::core::Error) -> (CameraFailure, String) {
    let code = error.code().0 as u32;
    (
        classify(code),
        format!("{} (0x{code:08X})", error.message().trim()),
    )
}

/// Opens the camera and reads frames until it fails or `stop` is set.
fn capture_once(
    preferred: Option<&str>,
    events: &Sender<CaptureEvent>,
    stop: &AtomicBool,
    sequence: &mut u64,
) -> Result<(), (CameraFailure, String)> {
    let fail = |error: windows::core::Error| hresult_failure(&error);
    let other = |text: String| (CameraFailure::Other, text);
    super::winmf::start_media_foundation().map_err(|e| other(e.to_string()))?;
    let api = super::mfapi::api().map_err(other)?;
    let candidates = super::winmf::video_capture_activations().map_err(|e| other(e.to_string()))?;
    let devices: Vec<VideoDevice> = candidates
        .iter()
        .map(|(_, device)| device.clone())
        .collect();
    let chosen = choose_physical(&devices, preferred)
        .ok_or((
            CameraFailure::NoCamera,
            "No physical camera is listed.".to_string(),
        ))?
        .clone();
    let activate = candidates
        .into_iter()
        .find(|(_, device)| *device == chosen)
        .map(|(activate, _)| activate)
        .ok_or((CameraFailure::Other, "The camera disappeared.".to_string()))?;
    // SAFETY: Media Foundation activation and a synchronous source reader used only on this
    // thread. Locked buffers are unlocked before the next call.
    unsafe {
        let source: IMFMediaSource = activate.ActivateObject().map_err(fail)?;
        let reader_attributes = api.create_attributes(2).map_err(fail)?;
        reader_attributes
            .SetUINT32(&MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1)
            .map_err(fail)?;
        let reader = api
            .create_source_reader(&source, Some(&reader_attributes))
            .map_err(fail)?;
        let stream = MF_SOURCE_READER_FIRST_VIDEO_STREAM.0 as u32;

        let mut opened = None;
        let mut last_error = None;
        for size in REQUESTED_SIZES {
            match select_type(api, &reader, stream, size) {
                Ok(()) => {
                    opened = Some(size);
                    break;
                }
                Err(error) => last_error = Some(error),
            }
        }
        let Some(requested) = opened else {
            let _ = source.Shutdown();
            return Err(last_error.map(|e| hresult_failure(&e)).unwrap_or((
                CameraFailure::Other,
                "The camera offers no usable size.".into(),
            )));
        };
        // The reader may have chosen another size than requested; read back what it delivers.
        let current = reader.GetCurrentMediaType(stream).map_err(fail)?;
        let packed = current.GetUINT64(&MF_MT_FRAME_SIZE).map_err(fail)?;
        let size = FrameSize::new((packed >> 32) as u32, packed as u32);
        let size = if size.is_valid() { size } else { requested };
        let stride = current
            .GetUINT32(&MF_MT_DEFAULT_STRIDE)
            .map(|s| (s as i32).unsigned_abs() as usize)
            .unwrap_or(size.width as usize);
        let _ = events.send(CaptureEvent::Started {
            size,
            device: chosen.name.clone(),
        });

        let result = read_loop(&reader, stream, size, stride, events, stop, sequence);
        let _ = source.Shutdown();
        result
    }
}

/// # Safety
/// `reader` must be a valid source reader.
unsafe fn select_type(
    api: &super::mfapi::Api,
    reader: &IMFSourceReader,
    stream: u32,
    size: FrameSize,
) -> windows::core::Result<()> {
    // SAFETY: plain Media Foundation attribute calls on a type created here.
    unsafe {
        let media_type = api.create_media_type()?;
        media_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
        media_type.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_NV12)?;
        media_type.SetUINT64(
            &MF_MT_FRAME_SIZE,
            (u64::from(size.width) << 32) | u64::from(size.height),
        )?;
        media_type.SetUINT64(&MF_MT_FRAME_RATE, (30u64 << 32) | 1)?;
        reader.SetCurrentMediaType(stream, None, &media_type)
    }
}

/// # Safety
/// `reader` must be a valid source reader that has a media type set.
unsafe fn read_loop(
    reader: &IMFSourceReader,
    stream: u32,
    size: FrameSize,
    stride: usize,
    events: &Sender<CaptureEvent>,
    stop: &AtomicBool,
    sequence: &mut u64,
) -> Result<(), (CameraFailure, String)> {
    // SAFETY: synchronous reads; each buffer is locked, copied, and unlocked before the next read.
    unsafe {
        while !stop.load(Ordering::SeqCst) {
            let mut flags = 0u32;
            let mut sample = None;
            reader
                .ReadSample(stream, 0, None, Some(&mut flags), None, Some(&mut sample))
                .map_err(|e| hresult_failure(&e))?;
            if flags & MF_SOURCE_READERF_ERROR.0 as u32 != 0 {
                return Err((CameraFailure::Gone, "The camera reported an error.".into()));
            }
            if flags & MF_SOURCE_READERF_ENDOFSTREAM.0 as u32 != 0 {
                return Err((CameraFailure::Gone, "The camera stopped.".into()));
            }
            let Some(sample) = sample else { continue };
            let buffer = sample
                .ConvertToContiguousBuffer()
                .map_err(|e| hresult_failure(&e))?;
            let mut data: *mut u8 = std::ptr::null_mut();
            let mut length = 0u32;
            buffer
                .Lock(&mut data, None, Some(&mut length))
                .map_err(|e| hresult_failure(&e))?;
            let packed = repack_nv12(
                std::slice::from_raw_parts(data, length as usize),
                size,
                stride,
            );
            let _ = buffer.Unlock();
            if let Some(packed) = packed {
                *sequence += 1;
                if let Some(frame) = Frame::new(size, packed, *sequence) {
                    if events.send(CaptureEvent::Frame(frame)).is_err() {
                        return Ok(());
                    }
                }
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const C920: &str = r"\\?\usb#vid_046d&pid_082d&mi_00#7&17e9de75&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";
    const INTEGRATED: &str = r"\\?\usb#vid_13d3&pid_56bb&mi_00#6&1ba4626f&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";
    const VIRTUAL: &str = r"\\?\swd#vcamdevapi#aaaa#{e5323777-f976-4f5b-9b55-b94699c46e44}\{11111111-1111-1111-1111-111111111111}";

    fn device(name: &str, link: &str) -> VideoDevice {
        VideoDevice {
            name: name.into(),
            symbolic_link: link.into(),
        }
    }

    fn listing() -> Vec<VideoDevice> {
        vec![
            device(
                "HyperHarbor Camera (Work) (Windows Virtual Camera)",
                VIRTUAL,
            ),
            device("HD Pro Webcam C920", C920),
            device("Integrated Camera", INTEGRATED),
        ]
    }

    #[test]
    fn the_first_physical_camera_is_chosen_and_virtual_ones_never_are() {
        let devices = listing();
        assert_eq!(
            choose_physical(&devices, None).unwrap().name,
            "HD Pro Webcam C920"
        );
    }

    #[test]
    fn a_preferred_camera_wins_when_it_is_listed() {
        let devices = listing();
        assert_eq!(
            choose_physical(&devices, Some(INTEGRATED)).unwrap().name,
            "Integrated Camera"
        );
        // Case differences in the path do not matter.
        assert_eq!(
            choose_physical(&devices, Some(&INTEGRATED.to_uppercase()))
                .unwrap()
                .name,
            "Integrated Camera"
        );
    }

    #[test]
    fn an_unplugged_preferred_camera_falls_back_to_the_first_physical_one() {
        let devices = listing();
        assert_eq!(
            choose_physical(&devices, Some(r"\\?\usb#gone"))
                .unwrap()
                .name,
            "HD Pro Webcam C920"
        );
    }

    #[test]
    fn a_virtual_camera_cannot_be_chosen_even_when_preferred() {
        let devices = listing();
        assert_eq!(
            choose_physical(&devices, Some(VIRTUAL)).unwrap().name,
            "HD Pro Webcam C920"
        );
        let only_virtual = vec![device("OBS Virtual Camera", r"\\?\swd#something#x")];
        assert!(choose_physical(&only_virtual, None).is_none());
        assert!(choose_physical(&[], None).is_none());
    }

    #[test]
    fn this_clients_cameras_are_excluded_by_name_too() {
        let devices = vec![device("HyperHarbor Camera (Work)", r"\\?\usb#odd")];
        assert!(choose_physical(&devices, None).is_none());
    }

    #[test]
    fn rows_without_padding_are_copied_as_they_are() {
        let size = FrameSize::new(4, 2);
        let src: Vec<u8> = (0..12).collect();
        assert_eq!(repack_nv12(&src, size, 4).unwrap(), src);
    }

    #[test]
    fn padded_rows_are_packed_tight() {
        // 4x2 frame with a stride of 6: two Y rows then one UV row, each 6 bytes apart.
        let size = FrameSize::new(4, 2);
        let src: Vec<u8> = vec![
            1, 2, 3, 4, 0, 0, // Y row 0
            5, 6, 7, 8, 0, 0, // Y row 1
            9, 10, 11, 12, 0, 0, // UV row 0
        ];
        assert_eq!(
            repack_nv12(&src, size, 6).unwrap(),
            vec![1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]
        );
    }

    #[test]
    fn a_short_buffer_or_a_bad_stride_is_refused() {
        let size = FrameSize::new(4, 2);
        assert!(repack_nv12(&[0; 11], size, 4).is_none());
        assert!(repack_nv12(&[0; 17], size, 6).is_none());
        assert!(
            repack_nv12(&[0; 12], size, 3).is_none(),
            "a stride below the width"
        );
        assert!(
            repack_nv12(&[0; 12], FrameSize::new(3, 2), 3).is_none(),
            "an odd width"
        );
    }

    #[test]
    fn virtual_device_paths_are_recognised() {
        assert!(is_virtual_link(VIRTUAL));
        assert!(is_virtual_link(&VIRTUAL.to_uppercase()));
        assert!(!is_virtual_link(C920));
    }
}
