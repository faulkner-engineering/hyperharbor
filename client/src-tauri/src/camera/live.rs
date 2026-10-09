//! Live checks of the whole camera path on a real Windows 11 device. Ignored by default; run with
//! `HH_VCAM_LIVE=1 cargo test --lib camera::live -- --ignored --nocapture`.
//!
//! They need the camera source DLL registered (`regsvr32 hyperharbor_vcam.dll`, elevated, with
//! the DLL somewhere the LocalService account can read). They create a real virtual camera, serve
//! frames to it through the pipe, and read the pictures back through Media Foundation the way an
//! application would.
#![cfg(test)]
#![allow(unsafe_code)]

use super::fanout::Sink;
use super::frame::{Frame, FrameSize};
use super::mfapi;
use super::naming;
use super::pipe::PipeSink;
use super::resolve::{wait_for_symbolic_link, DeviceEnumerator};
use super::winmf::{self, MfDeviceEnumerator, VirtualCamera};
use hyperharbor_vcam_protocol::{pipe_name, Status};
use std::time::{Duration, Instant};
use windows::Win32::Media::MediaFoundation::*;

fn enabled() -> bool {
    std::env::var("HH_VCAM_LIVE").is_ok_and(|v| v == "1")
}

/// Reads up to `count` frames from the video device whose friendly name starts with `name`, and
/// returns each frame's size and mean luma.
fn read_frames(name: &str, count: usize, within: Duration) -> Vec<((u32, u32), u8)> {
    let api = mfapi::api().expect("Media Foundation");
    let activate = winmf::video_capture_activations()
        .unwrap()
        .into_iter()
        .find(|(_, device)| device.name.starts_with(name))
        .map(|(activate, _)| activate)
        .expect("the virtual camera is not listed");
    // SAFETY: a synchronous source reader used on this thread; each locked buffer is read and
    // unlocked before the next sample.
    unsafe {
        let source: IMFMediaSource = activate.ActivateObject().unwrap();
        let reader = api.create_source_reader(&source, None).unwrap();
        let started = Instant::now();
        let mut frames = Vec::new();
        while frames.len() < count && started.elapsed() < within {
            let mut flags = 0u32;
            let mut sample = None;
            reader
                .ReadSample(
                    MF_SOURCE_READER_FIRST_VIDEO_STREAM.0 as u32,
                    0,
                    None,
                    Some(&mut flags),
                    None,
                    Some(&mut sample),
                )
                .unwrap();
            let Some(sample) = sample else { continue };
            let media_type = reader
                .GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM.0 as u32)
                .unwrap();
            let packed = media_type.GetUINT64(&MF_MT_FRAME_SIZE).unwrap();
            let size = ((packed >> 32) as u32, packed as u32);
            let buffer = sample.ConvertToContiguousBuffer().unwrap();
            let mut data: *mut u8 = std::ptr::null_mut();
            let mut length = 0u32;
            buffer.Lock(&mut data, None, Some(&mut length)).unwrap();
            let luma_len = (size.0 * size.1) as usize;
            let luma = std::slice::from_raw_parts(data, luma_len.min(length as usize));
            let mean = (luma.iter().step_by(97).map(|&b| u64::from(b)).sum::<u64>()
                / (luma.len() / 97).max(1) as u64) as u8;
            buffer.Unlock().unwrap();
            frames.push((size, mean));
        }
        let _ = source.Shutdown();
        frames
    }
}

fn solid(size: FrameSize, luma: u8, sequence: u64) -> Frame {
    let mut data = vec![128u8; size.nv12_len()];
    data[..(size.width * size.height) as usize].fill(luma);
    Frame::new(size, data, sequence).unwrap()
}

/// A virtual camera that shows what the pipe sends: the source runs in the Frame Server as
/// LocalService, so this proves the pipe's access list, the creator process check, and the
/// scaling to the consumer's size.
#[test]
#[ignore = "needs the camera source DLL registered; set HH_VCAM_LIVE=1"]
fn a_virtual_camera_shows_the_frames_the_client_sends() {
    if !enabled() {
        return;
    }
    assert!(
        winmf::detect_support().is_available(),
        "{:?}",
        winmf::detect_support()
    );
    let name = naming::friendly_name("LiveTest");
    let mut sink = PipeSink::start(&pipe_name(std::process::id(), &name)).unwrap();
    let camera = VirtualCamera::create(&name).unwrap();

    let link = wait_for_symbolic_link(
        &MfDeviceEnumerator,
        &name,
        Duration::from_secs(10),
        Duration::from_millis(100),
    )
    .unwrap();
    println!("link: {link}");

    // Send bright frames for a while, as the capture thread would.
    let feeder = std::thread::spawn(move || {
        let started = Instant::now();
        let mut sequence = 0;
        while started.elapsed() < Duration::from_secs(8) {
            sequence += 1;
            sink.send(Status::Live, &solid(FrameSize::HD, 200, sequence));
            std::thread::sleep(Duration::from_millis(33));
        }
    });

    let frames = read_frames(&name, 30, Duration::from_secs(8));
    println!(
        "{} frames; first {:?}, last {:?}",
        frames.len(),
        frames.first(),
        frames.last()
    );
    feeder.join().unwrap();
    drop(camera);

    assert!(frames.len() >= 15, "only {} frames arrived", frames.len());
    let bright = frames.iter().filter(|(_, luma)| *luma > 150).count();
    assert!(
        bright >= frames.len() / 2,
        "most frames should show the client's picture, not black: {frames:?}"
    );
    assert_eq!(frames[0].0, (1280, 720));
}

#[test]
#[ignore = "needs the camera source DLL registered; set HH_VCAM_LIVE=1"]
fn each_camera_shows_only_its_own_pipe() {
    if !enabled() {
        return;
    }
    let (work, games) = (
        naming::friendly_name("LiveWork"),
        naming::friendly_name("LiveGames"),
    );
    let mut work_sink = PipeSink::start(&pipe_name(std::process::id(), &work)).unwrap();
    let mut games_sink = PipeSink::start(&pipe_name(std::process::id(), &games)).unwrap();
    let work_camera = VirtualCamera::create(&work).unwrap();
    let games_camera = VirtualCamera::create(&games).unwrap();
    let links = [&work, &games].map(|n| {
        wait_for_symbolic_link(
            &MfDeviceEnumerator,
            n,
            Duration::from_secs(10),
            Duration::from_millis(100),
        )
        .unwrap()
    });
    assert_ne!(links[0], links[1]);

    let feeder = std::thread::spawn(move || {
        let started = Instant::now();
        let mut sequence = 0;
        while started.elapsed() < Duration::from_secs(8) {
            sequence += 1;
            work_sink.send(Status::Live, &solid(FrameSize::HD, 220, sequence));
            games_sink.send(Status::Live, &solid(FrameSize::HD, 60, sequence));
            std::thread::sleep(Duration::from_millis(33));
        }
    });
    let work_frames = read_frames(&work, 20, Duration::from_secs(8));
    let games_frames = read_frames(&games, 20, Duration::from_secs(8));
    feeder.join().unwrap();
    drop((work_camera, games_camera));

    let mean = |frames: &[((u32, u32), u8)]| {
        frames.iter().map(|f| u32::from(f.1)).sum::<u32>() / frames.len().max(1) as u32
    };
    println!(
        "work mean {}, games mean {}",
        mean(&work_frames),
        mean(&games_frames)
    );
    assert!(mean(&work_frames) > 180, "{work_frames:?}");
    assert!((40..100).contains(&mean(&games_frames)), "{games_frames:?}");
}

#[test]
#[ignore = "needs the camera source DLL registered; set HH_VCAM_LIVE=1"]
fn the_physical_cameras_are_listed_next_to_the_virtual_one() {
    if !enabled() {
        return;
    }
    let devices = MfDeviceEnumerator.video_devices().unwrap();
    println!("{devices:#?}");
    assert!(!devices.is_empty());
}

/// Opens the real physical camera for a moment (its light comes on), checks that NV12 frames of a
/// valid size arrive, and keeps no pixels.
#[test]
#[ignore = "opens the physical camera; set HH_VCAM_LIVE=1"]
fn the_physical_camera_delivers_nv12_frames() {
    if !enabled() {
        return;
    }
    use super::capture::{CaptureEvent, PhysicalCamera};
    let (sender, receiver) = std::sync::mpsc::channel();
    let camera = PhysicalCamera::start(None, sender);
    let started = Instant::now();
    let (mut frames, mut opened, mut failure) = (0, None, None);
    while started.elapsed() < Duration::from_secs(12) && frames < 30 {
        match receiver.recv_timeout(Duration::from_millis(500)) {
            Ok(CaptureEvent::Started { size, device }) => opened = Some((size, device)),
            Ok(CaptureEvent::Frame(frame)) => {
                assert!(frame.size.is_valid());
                assert_eq!(frame.data.len(), frame.size.nv12_len());
                frames += 1;
            }
            Ok(CaptureEvent::Failed { failure: f, detail }) => failure = Some((f, detail)),
            Err(_) => {}
        }
    }
    drop(camera);
    println!("opened: {opened:?}; frames: {frames}; failure: {failure:?}");
    assert!(opened.is_some(), "the camera did not open: {failure:?}");
    assert!(frames >= 10, "only {frames} frames");
}

/// The product code end to end: the camera service gives each of two VM sessions its own virtual
/// camera, and `rdp::launch` opens Remote Desktop to each with only that camera redirected. The VMs'
/// credentials come from the live host harness (`CameraConnectionLiveTests`, which writes them to
/// HH_CAMCONN_FILE and answers guest queries). The sessions stay open until `<file>.done` exists, so
/// the guests can be inspected meanwhile.
#[test]
#[ignore = "opens Remote Desktop sessions to the harness test VMs; set HH_VCAM_LIVE=1 and HH_CAMCONN_FILE"]
fn the_client_gives_two_vm_sessions_their_own_cameras() {
    use super::backend::WindowsBackend;
    use super::service::CameraService;
    use super::CameraRedirect;
    use crate::{monitors::MonitorLayout, rdp};
    use std::sync::Arc;

    if !enabled() {
        return;
    }
    let Ok(path) = std::env::var("HH_CAMCONN_FILE") else {
        return;
    };
    let connections: serde_json::Value =
        serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
    let ledger = std::env::temp_dir()
        .join(format!("hh-live-ledger-{}", std::process::id()))
        .join("ledger.json");
    let service = CameraService::new(Arc::new(WindowsBackend), None, ledger);
    let directory = rdp::file_directory();

    for name in ["HyperHarbor-Cam1", "HyperHarbor-Cam2"] {
        let connection: rdp::VmConnection =
            serde_json::from_value(connections[name].clone()).unwrap();
        let key = format!("live/{name}");
        let prepared = service.prepare(&key, name);
        println!(
            "{name}: redirect {:?}, notice {:?}",
            prepared.redirect, prepared.notice
        );
        assert!(
            matches!(prepared.redirect, CameraRedirect::Link(_)),
            "{prepared:?}"
        );
        let ending = service.clone();
        let end_key = key.clone();
        let pid = rdp::launch(
            &connection,
            &MonitorLayout::Single,
            &prepared.redirect,
            &directory,
            Some(Box::new(move || ending.end(&end_key))),
        )
        .unwrap();
        service.attach_process(&key, pid);
        println!("{name}: mstsc process {pid}");
    }

    let done = std::path::PathBuf::from(format!("{path}.done"));
    let started = Instant::now();
    while !done.exists() && started.elapsed() < Duration::from_secs(900) {
        std::thread::sleep(Duration::from_millis(500));
    }
    println!("status at the end: {:#?}", service.status());
    service.shutdown();
}

/// The camera for this PC's own applications: it appears with a VM's camera, shows the real
/// physical camera live, and goes when the VM's session ends.
#[test]
#[ignore = "needs the camera source registered and opens the physical camera; set HH_VCAM_LIVE=1"]
fn a_camera_for_this_pc_shows_live_video_while_a_vm_camera_exists_and_goes_with_it() {
    use super::backend::WindowsBackend;
    use super::service::CameraService;
    use std::sync::Arc;

    if !enabled() {
        return;
    }
    let ledger = std::env::temp_dir()
        .join(format!("hh-live-local-{}", std::process::id()))
        .join("ledger.json");
    let service = CameraService::new(Arc::new(WindowsBackend), None, ledger);
    let listed = || {
        MfDeviceEnumerator
            .video_devices()
            .unwrap()
            .into_iter()
            .map(|device| device.name)
            .collect::<Vec<_>>()
    };

    assert!(
        !listed().iter().any(|n| n.contains("This PC")),
        "{:?}",
        listed()
    );
    service.prepare("live/vm", "LiveVm");
    let local = service.status().local_camera.expect("a camera for this PC");
    assert_eq!(local, "HyperHarbor Camera (This PC)");
    let names = listed();
    println!("cameras while a VM is connected: {names:?}");
    assert!(names
        .iter()
        .any(|n| n.starts_with("HyperHarbor Camera (LiveVm)")));
    assert!(names
        .iter()
        .any(|n| n.starts_with("HyperHarbor Camera (This PC)")));

    // Read it as an application on this PC would: real frames from the physical camera, live.
    let frames = read_frames(&local, 30, Duration::from_secs(10));
    println!("{} frames; first {:?}", frames.len(), frames.first());
    assert!(frames.len() >= 15, "only {} frames", frames.len());
    assert_eq!(frames[0].0, (1280, 720));
    assert!(
        frames.iter().any(|(_, luma)| *luma > 20),
        "all black: {frames:?}"
    );

    service.end("live/vm");
    let started = Instant::now();
    // Only this test's own cameras count, so a client running at the same time does not matter.
    let mine = |n: &String| {
        n.starts_with("HyperHarbor Camera (LiveVm)")
            || n.starts_with("HyperHarbor Camera (This PC)")
    };
    while listed().iter().any(mine) && started.elapsed() < Duration::from_secs(10) {
        std::thread::sleep(Duration::from_millis(200));
    }
    let after = listed();
    println!("cameras after the VM ended: {after:?}");
    assert!(!after.iter().any(mine), "{after:?}");
    service.shutdown();
}

type Size = (u32, u32);
/// A sample's size as the reader reports it, and the bytes in its buffer.
type Sampled = (Size, u32);

/// Reads frames the way an application that chooses a frame size does: it selects the offered type
/// with `want` as its size, then reads. Returns, for each sample, the size the reader reports and
/// the number of bytes in the buffer, so a mismatch (a 720p buffer for a 640x480 type) shows.
fn read_frames_choosing(name: &str, want: Size, count: usize) -> (Vec<Size>, Vec<Sampled>) {
    let api = mfapi::api().expect("Media Foundation");
    let activate = winmf::video_capture_activations()
        .unwrap()
        .into_iter()
        .find(|(_, device)| device.name.starts_with(name))
        .map(|(activate, _)| activate)
        .expect("the virtual camera is not listed");
    // SAFETY: a synchronous source reader used on this thread; each locked buffer is only measured.
    unsafe {
        let source: IMFMediaSource = activate.ActivateObject().unwrap();
        let reader = api.create_source_reader(&source, None).unwrap();
        let stream = MF_SOURCE_READER_FIRST_VIDEO_STREAM.0 as u32;
        let mut offered = Vec::new();
        let mut index = 0;
        while let Ok(media_type) = reader.GetNativeMediaType(stream, index) {
            let packed = media_type.GetUINT64(&MF_MT_FRAME_SIZE).unwrap();
            let size = ((packed >> 32) as u32, packed as u32);
            offered.push(size);
            if size == want {
                reader
                    .SetCurrentMediaType(stream, None, &media_type)
                    .unwrap();
            }
            index += 1;
        }
        let started = Instant::now();
        let mut frames = Vec::new();
        while frames.len() < count && started.elapsed() < Duration::from_secs(10) {
            let mut flags = 0u32;
            let mut sample = None;
            reader
                .ReadSample(stream, 0, None, Some(&mut flags), None, Some(&mut sample))
                .unwrap();
            let Some(sample) = sample else { continue };
            let current = reader.GetCurrentMediaType(stream).unwrap();
            let packed = current.GetUINT64(&MF_MT_FRAME_SIZE).unwrap();
            let buffer = sample.ConvertToContiguousBuffer().unwrap();
            frames.push((
                ((packed >> 32) as u32, packed as u32),
                buffer.GetCurrentLength().unwrap(),
            ));
        }
        let _ = source.Shutdown();
        (offered, frames)
    }
}

/// Only 1280x720 is offered, and every consumer gets it, however many others looked at the camera
/// and tried to pick something else before. (When 640x480 was also offered, an application that
/// probed the formats and picked it left the Windows Frame Server's cached descriptor at 640x480, so
/// the next application started in 640x480 until it asked again.)
#[test]
#[ignore = "needs the camera source registered and opens the physical camera; set HH_VCAM_LIVE=1"]
fn every_consumer_gets_720p_whatever_an_earlier_one_tried() {
    use super::backend::WindowsBackend;
    use super::service::CameraService;
    use std::sync::Arc;

    if !enabled() {
        return;
    }
    let ledger = std::env::temp_dir()
        .join(format!("hh-live-size-{}", std::process::id()))
        .join("ledger.json");
    let service = CameraService::new(Arc::new(WindowsBackend), None, ledger);
    let prepared = service.prepare("live/vm", "LiveVm");
    let local = service
        .status()
        .local_camera
        .unwrap_or_else(|| panic!("no camera for this PC: {prepared:?}"));

    let mut failures = Vec::new();
    // A consumer that chooses nothing, one that tries 640x480 (not offered, so nothing is selected),
    // one that picks 1280x720, and the default again, repeatedly.
    for want in [
        (0u32, 0u32),
        (640, 480),
        (1280, 720),
        (0, 0),
        (640, 480),
        (0, 0),
    ] {
        let (offered, frames) = read_frames_choosing(&local, want, 15);
        let wrong: Vec<_> = frames
            .iter()
            .filter(|(size, len)| *size != (1280, 720) || *len != 1_382_400)
            .collect();
        println!(
            "asked for {want:?}; offered {offered:?}: {} frames, {} wrong, first {:?}",
            frames.len(),
            wrong.len(),
            frames.first()
        );
        if offered != [(1280, 720)] || frames.len() < 10 || !wrong.is_empty() {
            failures.push(format!(
                "{want:?}: offered {offered:?}, {} frames, wrong: {:?}",
                frames.len(),
                wrong.first()
            ));
        }
    }
    service.end("live/vm");
    service.shutdown();
    assert!(failures.is_empty(), "{failures:#?}");
}
