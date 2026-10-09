//! The virtual camera's media source and its one video stream. Structure and attributes follow
//! what the Windows Frame Server asked for in testing (IMFMediaSourceEx, IMFGetService, IKsControl,
//! IMFSampleAllocatorControl); frames come from the client through `reader`.

use crate::reader::{self, Shared};
use hyperharbor_vcam_protocol::nv12;
use std::ffi::c_void;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use windows::core::*;
use windows::Win32::Foundation::*;
use windows::Win32::Media::KernelStreaming::*;
use windows::Win32::Media::MediaFoundation::*;
use windows::Win32::System::Com::StructuredStorage::PROPVARIANT;

/// The sizes offered: 1280x720 only.
///
/// A second size (640x480 was offered before) made the camera start in the wrong size. The Windows
/// Frame Server keeps one descriptor for the camera and reuses it for every application, updating
/// its current type whenever any application picks one; an application that probes the formats and
/// picks 640x480 therefore made 640x480 the default of the next application, which stayed at
/// 640x480 until it asked again. The source cannot reset that cache (it is called with
/// `Start` and sees the cached type, and `CreatePresentationDescriptor` is called once). With one size
/// there is nothing to stick. Applications that want a smaller picture scale this one themselves;
/// the client already scales whatever size the physical camera produces up to it.
pub const SIZES: [(u32, u32); 1] = [(1280, 720)];
const FRAME_INTERVAL_US: u64 = 33_333;

fn media_type(width: u32, height: u32) -> Result<IMFMediaType> {
    // SAFETY: plain Media Foundation attribute calls on a type created here.
    unsafe {
        let t = MFCreateMediaType()?;
        t.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
        t.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_NV12)?;
        t.SetUINT64(
            &MF_MT_FRAME_SIZE,
            (u64::from(width) << 32) | u64::from(height),
        )?;
        t.SetUINT64(&MF_MT_FRAME_RATE, (30u64 << 32) | 1)?;
        t.SetUINT64(&MF_MT_PIXEL_ASPECT_RATIO, (1u64 << 32) | 1)?;
        t.SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)?;
        t.SetUINT32(&MF_MT_ALL_SAMPLES_INDEPENDENT, 1)?;
        t.SetUINT32(&MF_MT_DEFAULT_STRIDE, width)?;
        t.SetUINT32(&MF_MT_SAMPLE_SIZE, width * height * 3 / 2)?;
        Ok(t)
    }
}

/// A stream descriptor offering every size, with the preferred one (the first) current, and the
/// presentation descriptor that holds it.
fn descriptors() -> Result<(IMFStreamDescriptor, IMFPresentationDescriptor)> {
    // SAFETY: plain Media Foundation object creation and attribute calls.
    unsafe {
        let types: Vec<Option<IMFMediaType>> = SIZES
            .iter()
            .map(|&(w, h)| media_type(w, h).map(Some))
            .collect::<Result<_>>()?;
        let sd = MFCreateStreamDescriptor(0, &types)?;
        if let Some(Some(preferred)) = types.first() {
            sd.GetMediaTypeHandler()?.SetCurrentMediaType(preferred)?;
        }
        sd.SetGUID(&MF_DEVICESTREAM_STREAM_CATEGORY, &PINNAME_VIDEO_CAPTURE)?;
        sd.SetUINT32(&MF_DEVICESTREAM_STREAM_ID, 0)?;
        sd.SetUINT32(&MF_DEVICESTREAM_FRAMESERVER_SHARED, 1)?;
        sd.SetUINT32(
            &MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES,
            MFFrameSourceTypes_Color.0 as u32,
        )?;
        let pd = MFCreatePresentationDescriptor(Some(&[Some(sd.clone())]))?;
        pd.SelectStream(0)?;
        Ok((sd, pd))
    }
}

#[implement(IMFMediaSourceEx, IMFGetService, IKsControl, IMFSampleAllocatorControl)]
pub struct Source {
    queue: IMFMediaEventQueue,
    pd: IMFPresentationDescriptor,
    /// The stream's descriptor: its current type is the size being produced.
    sd: IMFStreamDescriptor,
    attrs: IMFAttributes,
    shared: Arc<Shared>,
    started: Mutex<bool>,
}

impl Source {
    /// A source whose frames arrive on `pipe`, served by process `creator_pid`.
    pub fn new(pipe: String, creator_pid: u32) -> Result<Self> {
        // SAFETY: plain Media Foundation object creation and attribute calls.
        unsafe {
            let queue = MFCreateEventQueue()?;
            let (sd, pd) = descriptors()?;
            let mut attrs = None;
            MFCreateAttributes(&mut attrs, 4)?;
            let shared = Shared::new();
            reader::start(shared.clone(), pipe, creator_pid);
            Ok(Self {
                queue,
                pd,
                sd,
                attrs: attrs.ok_or_else(|| Error::from(E_FAIL))?,
                shared,
                started: Mutex::new(false),
            })
        }
    }
}

impl IMFMediaEventGenerator_Impl for Source_Impl {
    fn GetEvent(&self, flags: MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS) -> Result<IMFMediaEvent> {
        // SAFETY: forwards to the event queue this object owns.
        unsafe { self.queue.GetEvent(flags.0) }
    }
    fn BeginGetEvent(&self, cb: Ref<IMFAsyncCallback>, state: Ref<IUnknown>) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.queue.BeginGetEvent(cb.as_ref(), state.as_ref()) }
    }
    fn EndGetEvent(&self, result: Ref<IMFAsyncResult>) -> Result<IMFMediaEvent> {
        // SAFETY: as above.
        unsafe { self.queue.EndGetEvent(result.as_ref()) }
    }
    fn QueueEvent(
        &self,
        met: u32,
        extended: *const GUID,
        status: HRESULT,
        value: *const PROPVARIANT,
    ) -> Result<()> {
        // SAFETY: the pointers come from the caller of this COM method.
        unsafe { self.queue.QueueEventParamVar(met, extended, status, value) }
    }
}

impl IMFMediaSource_Impl for Source_Impl {
    fn GetCharacteristics(&self) -> Result<u32> {
        Ok(MFMEDIASOURCE_IS_LIVE.0 as u32)
    }
    fn CreatePresentationDescriptor(&self) -> Result<IMFPresentationDescriptor> {
        // SAFETY: clones the descriptor this object owns.
        unsafe { self.pd.Clone() }
    }
    fn Start(
        &self,
        _descriptor: Ref<IMFPresentationDescriptor>,
        _time_format: *const GUID,
        _position: *const PROPVARIANT,
    ) -> Result<()> {
        // SAFETY: Media Foundation object creation and event queueing on objects owned here.
        unsafe {
            let this: IMFMediaSourceEx = self.to_interface();
            let source: IMFMediaSource = this.cast()?;
            let stream_queue = MFCreateEventQueue()?;
            let stream: IMFMediaStream2 = MediaStream {
                source,
                sd: self.sd.clone(),
                queue: stream_queue.clone(),
                shared: self.shared.clone(),
                clock: Mutex::new((Instant::now(), 0)),
            }
            .into();
            let unknown: IUnknown = stream.cast()?;
            let new_stream = if self.started.lock().map(|s| *s).unwrap_or(false) {
                MEUpdatedStream
            } else {
                MENewStream
            };
            self.queue
                .QueueEventParamUnk(new_stream.0 as u32, &GUID::zeroed(), S_OK, &unknown)?;
            self.queue.QueueEventParamVar(
                MESourceStarted.0 as u32,
                &GUID::zeroed(),
                S_OK,
                &PROPVARIANT::default(),
            )?;
            stream_queue.QueueEventParamVar(
                MEStreamStarted.0 as u32,
                &GUID::zeroed(),
                S_OK,
                &PROPVARIANT::default(),
            )?;
            if let Ok(mut started) = self.started.lock() {
                *started = true;
            }
        }
        Ok(())
    }
    fn Stop(&self) -> Result<()> {
        Ok(())
    }
    fn Pause(&self) -> Result<()> {
        Err(MF_E_INVALID_STATE_TRANSITION.into())
    }
    fn Shutdown(&self) -> Result<()> {
        self.shared.stop();
        Ok(())
    }
}

impl IMFMediaSourceEx_Impl for Source_Impl {
    fn GetSourceAttributes(&self) -> Result<IMFAttributes> {
        Ok(self.attrs.clone())
    }
    fn GetStreamAttributes(&self, _stream_id: u32) -> Result<IMFAttributes> {
        self.sd.cast()
    }
    fn SetD3DManager(&self, _manager: Ref<IUnknown>) -> Result<()> {
        Err(E_NOTIMPL.into())
    }
}

impl IMFGetService_Impl for Source_Impl {
    fn GetService(
        &self,
        _service: *const GUID,
        _iid: *const GUID,
        _out: *mut *mut c_void,
    ) -> Result<()> {
        Err(MF_E_UNSUPPORTED_SERVICE.into())
    }
}

/// ERROR_SET_NOT_FOUND: the answer to every camera control property, since there are none.
const NOT_FOUND: HRESULT = HRESULT(0x8007_0492_u32 as i32);

impl IKsControl_Impl for Source_Impl {
    fn KsProperty(
        &self,
        _property: *const KSIDENTIFIER,
        _length: u32,
        _data: *mut c_void,
        _data_length: u32,
        _returned: *mut u32,
    ) -> Result<()> {
        Err(NOT_FOUND.into())
    }
    fn KsMethod(
        &self,
        _method: *const KSIDENTIFIER,
        _length: u32,
        _data: *mut c_void,
        _data_length: u32,
        _returned: *mut u32,
    ) -> Result<()> {
        Err(NOT_FOUND.into())
    }
    fn KsEvent(
        &self,
        _event: *const KSIDENTIFIER,
        _length: u32,
        _data: *mut c_void,
        _data_length: u32,
        _returned: *mut u32,
    ) -> Result<()> {
        Err(NOT_FOUND.into())
    }
}

impl IMFSampleAllocatorControl_Impl for Source_Impl {
    fn SetDefaultAllocator(&self, _stream: u32, _allocator: Ref<IUnknown>) -> Result<()> {
        Ok(())
    }
    fn GetAllocatorUsage(
        &self,
        stream: u32,
        input: *mut u32,
        usage: *mut MFSampleAllocatorUsage,
    ) -> Result<()> {
        // SAFETY: both pointers are valid out parameters supplied by the caller.
        unsafe {
            *input = stream;
            *usage = MFSampleAllocatorUsage_DoesNotAllocate;
        }
        Ok(())
    }
}

#[implement(IMFMediaStream2)]
struct MediaStream {
    source: IMFMediaSource,
    sd: IMFStreamDescriptor,
    queue: IMFMediaEventQueue,
    shared: Arc<Shared>,
    /// When the stream started and how many frames it has produced, for pacing.
    clock: Mutex<(Instant, u64)>,
}

impl MediaStream_Impl {
    /// The size the consumer chose, or the preferred one.
    fn current_size(&self) -> (u32, u32) {
        // SAFETY: reads the current media type of the descriptor this stream owns.
        unsafe {
            self.sd
                .GetMediaTypeHandler()
                .and_then(|handler| handler.GetCurrentMediaType())
                .and_then(|t| t.GetUINT64(&MF_MT_FRAME_SIZE))
                .map(|packed| ((packed >> 32) as u32, packed as u32))
                .unwrap_or(SIZES[0])
        }
    }

    /// One frame of `size`: the client's newest frame scaled to fit, or black before the first.
    fn frame_for(&self, size: (u32, u32)) -> Vec<u8> {
        match self.shared.latest() {
            Some(latest) if (latest.width, latest.height) == size => latest.data.as_ref().clone(),
            Some(latest) => nv12::scale(&latest.data, (latest.width, latest.height), size),
            None => nv12::black(size.0, size.1),
        }
    }
}

impl IMFMediaEventGenerator_Impl for MediaStream_Impl {
    fn GetEvent(&self, flags: MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS) -> Result<IMFMediaEvent> {
        // SAFETY: forwards to the event queue this object owns.
        unsafe { self.queue.GetEvent(flags.0) }
    }
    fn BeginGetEvent(&self, cb: Ref<IMFAsyncCallback>, state: Ref<IUnknown>) -> Result<()> {
        // SAFETY: as above.
        unsafe { self.queue.BeginGetEvent(cb.as_ref(), state.as_ref()) }
    }
    fn EndGetEvent(&self, result: Ref<IMFAsyncResult>) -> Result<IMFMediaEvent> {
        // SAFETY: as above.
        unsafe { self.queue.EndGetEvent(result.as_ref()) }
    }
    fn QueueEvent(
        &self,
        met: u32,
        extended: *const GUID,
        status: HRESULT,
        value: *const PROPVARIANT,
    ) -> Result<()> {
        // SAFETY: the pointers come from the caller of this COM method.
        unsafe { self.queue.QueueEventParamVar(met, extended, status, value) }
    }
}

impl IMFMediaStream_Impl for MediaStream_Impl {
    fn GetMediaSource(&self) -> Result<IMFMediaSource> {
        Ok(self.source.clone())
    }
    fn GetStreamDescriptor(&self) -> Result<IMFStreamDescriptor> {
        Ok(self.sd.clone())
    }
    fn RequestSample(&self, token: Ref<IUnknown>) -> Result<()> {
        // Pace at 30 fps: the consumer asks again as soon as it has a sample. A stream that fell
        // far behind (the machine slept) restarts its clock instead of racing to catch up.
        let wait = {
            let mut clock = self.clock.lock().map_err(|_| Error::from(E_FAIL))?;
            let due = clock.0 + Duration::from_micros(clock.1 * FRAME_INTERVAL_US);
            if due + Duration::from_secs(1) < Instant::now() {
                *clock = (Instant::now(), 0);
            }
            clock.1 += 1;
            due.saturating_duration_since(Instant::now())
        };
        std::thread::sleep(wait);

        let size = self.current_size();
        let data = self.frame_for(size);
        // SAFETY: Media Foundation buffer and sample creation; the locked pointer is used only
        // between Lock and Unlock and the copy never exceeds the buffer's size.
        unsafe {
            let buffer = MFCreateMemoryBuffer(data.len() as u32)?;
            let mut pointer: *mut u8 = std::ptr::null_mut();
            buffer.Lock(&mut pointer, None, None)?;
            std::ptr::copy_nonoverlapping(data.as_ptr(), pointer, data.len());
            buffer.Unlock()?;
            buffer.SetCurrentLength(data.len() as u32)?;
            let sample = MFCreateSample()?;
            sample.AddBuffer(&buffer)?;
            // The Frame Server needs timestamps on the system clock; frame numbers from zero stall
            // its readers (observed).
            let now = MFGetSystemTime();
            sample.SetSampleTime(now)?;
            sample.SetSampleDuration(FRAME_INTERVAL_US as i64 * 10)?;
            sample.SetUINT64(&MFSampleExtension_DeviceTimestamp, now as u64)?;
            if let Some(token) = token.as_ref() {
                sample.SetUnknown(&MFSampleExtension_Token, token)?;
            }
            let unknown: IUnknown = sample.cast()?;
            self.queue.QueueEventParamUnk(
                MEMediaSample.0 as u32,
                &GUID::zeroed(),
                S_OK,
                &unknown,
            )?;
        }
        Ok(())
    }
}

impl IMFMediaStream2_Impl for MediaStream_Impl {
    fn SetStreamState(&self, _state: MF_STREAM_STATE) -> Result<()> {
        Ok(())
    }
    fn GetStreamState(&self) -> Result<MF_STREAM_STATE> {
        Ok(MF_STREAM_STATE_RUNNING)
    }
}
