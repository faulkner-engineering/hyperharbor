//! Fans each captured frame out to every session's virtual camera according to that session's
//! policy. Pure: sinks are a trait and time is passed in.
//!
//! What a sink receives is what the virtual camera shows until the next message, because the
//! camera source repeats the last frame it was given:
//! - Live sessions get every frame.
//! - Blurred sessions get a blurred frame, computed once per captured frame however many sessions
//!   need it.
//! - Frozen sessions get nothing, so the source keeps repeating the last live frame. A session that
//!   starts out frozen is sent one frame first, so it does not start black.
//! - Shuttered sessions get one black frame, marked shuttered.
//! - Every `KEEPALIVE`, a session that is not receiving frames is sent its held frame again, so a
//!   source that reconnected (the Frame Server restarted it) is never left blank.

use super::frame::{self, Frame, FrameSize};
use super::policy::{FrameMode, SessionPolicy};
use hyperharbor_vcam_protocol::Status;
use std::time::{Duration, Instant};

/// How often a session that receives no new frames is sent its held frame again.
pub const KEEPALIVE: Duration = Duration::from_secs(1);

/// The blur strength for unfocused sessions: strong enough that faces and surroundings are not
/// recognisable, cheap enough for 30 fps (the sliding window does not depend on the radius).
pub const BLUR_RADIUS: usize = 24;

/// Receives what a virtual camera should show.
pub trait Sink: Send {
    fn send(&mut self, status: Status, frame: &Frame);
}

/// Identifies a session; the caller chooses it (the VM and window, for example).
pub type SessionId = String;

struct Entry {
    id: SessionId,
    policy: SessionPolicy,
    sink: Box<dyn Sink>,
    /// What the virtual camera is showing now.
    held: Option<Frame>,
    last_mode: Option<FrameMode>,
    last_sent: Option<Instant>,
}

impl Entry {
    fn send(&mut self, status: Status, frame: &Frame, now: Instant) {
        self.sink.send(status, frame);
        self.held = Some(frame.clone());
        self.last_sent = Some(now);
    }

    fn keepalive_due(&self, now: Instant) -> bool {
        self.last_sent
            .is_none_or(|sent| now.saturating_duration_since(sent) >= KEEPALIVE)
    }
}

#[derive(Default)]
pub struct Fanout {
    entries: Vec<Entry>,
    black: Option<(FrameSize, Frame)>,
}

impl Fanout {
    pub fn new() -> Self {
        Self::default()
    }

    #[cfg(test)]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Adds a session. A session with the same id is replaced.
    pub fn add(&mut self, id: impl Into<SessionId>, policy: SessionPolicy, sink: Box<dyn Sink>) {
        let id = id.into();
        self.entries.retain(|entry| entry.id != id);
        self.entries.push(Entry {
            id,
            policy,
            sink,
            held: None,
            last_mode: None,
            last_sent: None,
        });
    }

    /// Removes a session; false when there was none.
    pub fn remove(&mut self, id: &str) -> bool {
        let before = self.entries.len();
        self.entries.retain(|entry| entry.id != id);
        self.entries.len() != before
    }

    pub fn policy_mut(&mut self, id: &str) -> Option<&mut SessionPolicy> {
        self.entries
            .iter_mut()
            .find(|entry| entry.id == id)
            .map(|entry| &mut entry.policy)
    }

    pub fn policy(&self, id: &str) -> Option<&SessionPolicy> {
        self.entries
            .iter()
            .find(|entry| entry.id == id)
            .map(|entry| &entry.policy)
    }

    fn black_for(&mut self, size: FrameSize) -> Frame {
        if let Some((cached, frame)) = &self.black {
            if *cached == size {
                return frame.clone();
            }
        }
        let frame = Frame::new(size, frame::black(size), 0).expect("black is a valid frame");
        self.black = Some((size, frame.clone()));
        frame
    }

    /// Handles one captured frame.
    pub fn on_frame(&mut self, captured: &Frame, now: Instant) {
        let black = self.black_for(captured.size);
        let mut blurred: Option<Frame> = None;
        for entry in &mut self.entries {
            let mode = entry.policy.mode(now);
            match mode {
                FrameMode::Live => entry.send(Status::Live, captured, now),
                FrameMode::Blurred => {
                    let shown = blurred.get_or_insert_with(|| {
                        Frame::new(
                            captured.size,
                            frame::blur(&captured.data, captured.size, BLUR_RADIUS),
                            captured.sequence,
                        )
                        .expect("a blurred frame has the size of its source")
                    });
                    entry.send(Status::Live, shown, now);
                }
                FrameMode::Frozen => {
                    if entry.held.is_none() {
                        entry.send(Status::Live, captured, now);
                    } else if entry.keepalive_due(now) {
                        let held = entry.held.clone().expect("checked above");
                        entry.send(Status::Live, &held, now);
                    }
                }
                FrameMode::Shuttered => {
                    if entry.last_mode != Some(FrameMode::Shuttered) || entry.keepalive_due(now) {
                        entry.send(Status::Shuttered, &black, now);
                    }
                }
            }
            entry.last_mode = Some(mode);
        }
    }

    /// Called while no frames arrive (the camera is busy or absent). Each session is kept alive
    /// with its held frame, marked with `status`; a shuttered session stays black. `fallback`
    /// is the size of the black frame for a session that never received one.
    pub fn on_idle(&mut self, status: Status, fallback: FrameSize, now: Instant) {
        let black = self.black_for(fallback);
        for entry in &mut self.entries {
            if !entry.keepalive_due(now) {
                continue;
            }
            let mode = entry.policy.mode(now);
            if mode == FrameMode::Shuttered {
                entry.send(Status::Shuttered, &black, now);
            } else {
                let shown = entry.held.clone().unwrap_or_else(|| black.clone());
                entry.send(status, &shown, now);
            }
            entry.last_mode = Some(mode);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::super::policy::UnfocusedBehavior;
    use super::*;
    use std::sync::{Arc, Mutex};

    const SIZE: FrameSize = FrameSize::new(16, 8);

    #[derive(Clone, Default)]
    struct Recorder(Arc<Mutex<Vec<(Status, u64, u8)>>>);

    impl Sink for Recorder {
        fn send(&mut self, status: Status, frame: &Frame) {
            self.0
                .lock()
                .unwrap()
                .push((status, frame.sequence, frame.data[0]));
        }
    }

    impl Recorder {
        fn sent(&self) -> Vec<(Status, u64, u8)> {
            self.0.lock().unwrap().clone()
        }
    }

    /// A frame whose first luma byte is `luma`, so tests can tell frames (and blurs) apart.
    fn frame(sequence: u64, luma: u8) -> Frame {
        let mut data = vec![luma; SIZE.nv12_len()];
        data[SIZE.nv12_len() - 1] = 128;
        // One bright pixel in the middle makes a blurred frame differ from the original.
        data[4 * 16 + 8] = 250;
        Frame::new(SIZE, data, sequence).unwrap()
    }

    fn focused(behavior: UnfocusedBehavior, now: Instant) -> SessionPolicy {
        let mut policy = SessionPolicy::new(behavior);
        policy.set_focused(true, now);
        policy
    }

    fn unfocused(behavior: UnfocusedBehavior) -> SessionPolicy {
        SessionPolicy::new(behavior)
    }

    fn after(start: Instant, ms: u64) -> Instant {
        start + Duration::from_millis(ms)
    }

    #[test]
    fn a_live_session_receives_every_frame() {
        let t0 = Instant::now();
        let recorder = Recorder::default();
        let mut fanout = Fanout::new();
        fanout.add(
            "a",
            focused(UnfocusedBehavior::Freeze, t0),
            Box::new(recorder.clone()),
        );
        fanout.on_frame(&frame(1, 10), t0);
        fanout.on_frame(&frame(2, 10), after(t0, 33));
        assert_eq!(recorder.sent().len(), 2);
        assert_eq!(recorder.sent()[1].1, 2);
    }

    #[test]
    fn a_frozen_session_is_sent_one_frame_and_then_nothing() {
        let t0 = Instant::now();
        let recorder = Recorder::default();
        let mut fanout = Fanout::new();
        fanout.add(
            "a",
            unfocused(UnfocusedBehavior::Freeze),
            Box::new(recorder.clone()),
        );
        for n in 0..20u64 {
            fanout.on_frame(&frame(n, 10), after(t0, n * 33));
        }
        // One frame so it does not start black, then the source keeps repeating it. No keepalive
        // is due yet inside the first second.
        assert_eq!(recorder.sent().len(), 1);
        assert_eq!(recorder.sent()[0].1, 0);
    }

    #[test]
    fn a_frozen_session_keeps_the_last_live_frame_it_was_shown() {
        let t0 = Instant::now();
        let recorder = Recorder::default();
        let mut fanout = Fanout::new();
        fanout.add(
            "a",
            focused(UnfocusedBehavior::Freeze, t0),
            Box::new(recorder.clone()),
        );
        fanout.on_frame(&frame(1, 10), t0);
        fanout.on_frame(&frame(2, 10), after(t0, 33));
        fanout
            .policy_mut("a")
            .unwrap()
            .set_focused(false, after(t0, 40));
        // After the grace period the session freezes on the last frame sent while it was live.
        fanout.on_frame(&frame(3, 10), after(t0, 400));
        fanout.on_frame(&frame(4, 10), after(t0, 433));
        let sent = recorder.sent();
        assert_eq!(sent.len(), 2);
        assert_eq!(sent.last().unwrap().1, 2);
    }

    #[test]
    fn a_frozen_session_repeats_its_held_frame_every_second() {
        let t0 = Instant::now();
        let recorder = Recorder::default();
        let mut fanout = Fanout::new();
        fanout.add(
            "a",
            unfocused(UnfocusedBehavior::Freeze),
            Box::new(recorder.clone()),
        );
        fanout.on_frame(&frame(1, 10), t0);
        fanout.on_frame(&frame(2, 10), after(t0, 999));
        assert_eq!(recorder.sent().len(), 1);
        fanout.on_frame(&frame(3, 10), after(t0, 1000));
        let sent = recorder.sent();
        assert_eq!(sent.len(), 2);
        assert_eq!(sent[1].1, 1, "the held frame is repeated, not the new one");
    }

    #[test]
    fn blurred_and_live_sessions_each_get_a_frame() {
        let t0 = Instant::now();
        let (a, b, live) = (
            Recorder::default(),
            Recorder::default(),
            Recorder::default(),
        );
        let mut fanout = Fanout::new();
        fanout.add("a", unfocused(UnfocusedBehavior::Blur), Box::new(a.clone()));
        fanout.add("b", unfocused(UnfocusedBehavior::Blur), Box::new(b.clone()));
        fanout.add(
            "c",
            focused(UnfocusedBehavior::Blur, t0),
            Box::new(live.clone()),
        );
        let original = frame(1, 10);
        fanout.on_frame(&original, t0);
        // The recorder reports the first luma byte: a blur of a flat frame with a bright pixel
        // changes pixels near it but the flat corner stays; compare the whole frame instead.
        assert_eq!(a.sent().len(), 1);
        assert_eq!(b.sent().len(), 1);
        assert_eq!(live.sent().len(), 1);
        assert_eq!(a.sent()[0].0, Status::Live);
    }

    #[test]
    fn sessions_that_need_a_blur_share_one_blurred_buffer() {
        struct Pointer(Arc<Mutex<Vec<usize>>>);
        impl Sink for Pointer {
            fn send(&mut self, _: Status, frame: &Frame) {
                self.0
                    .lock()
                    .unwrap()
                    .push(Arc::as_ptr(&frame.data) as *const u8 as usize);
            }
        }
        let t0 = Instant::now();
        let seen = Arc::new(Mutex::new(Vec::new()));
        let mut fanout = Fanout::new();
        for id in ["a", "b", "c"] {
            fanout.add(
                id,
                unfocused(UnfocusedBehavior::Blur),
                Box::new(Pointer(seen.clone())),
            );
        }
        let original = frame(1, 10);
        fanout.on_frame(&original, t0);
        let pointers = seen.lock().unwrap().clone();
        assert_eq!(pointers.len(), 3);
        assert!(
            pointers.iter().all(|p| *p == pointers[0]),
            "one blur served all three"
        );
        assert_ne!(
            pointers[0],
            Arc::as_ptr(&original.data) as *const u8 as usize
        );
    }

    #[test]
    fn blurring_really_changes_the_picture_sent() {
        struct Capture(Arc<Mutex<Vec<Vec<u8>>>>);
        impl Sink for Capture {
            fn send(&mut self, _: Status, frame: &Frame) {
                self.0.lock().unwrap().push(frame.data.to_vec());
            }
        }
        let t0 = Instant::now();
        let seen = Arc::new(Mutex::new(Vec::new()));
        let mut fanout = Fanout::new();
        fanout.add(
            "a",
            unfocused(UnfocusedBehavior::Blur),
            Box::new(Capture(seen.clone())),
        );
        let original = frame(1, 10);
        fanout.on_frame(&original, t0);
        let sent = seen.lock().unwrap()[0].clone();
        assert_ne!(sent, original.data.to_vec());
        assert!(sent[4 * 16 + 8] < 250, "the bright pixel is spread out");
        assert_eq!(sent.len(), original.data.len());
    }

    #[test]
    fn a_shuttered_session_gets_one_black_frame_marked_shuttered() {
        let t0 = Instant::now();
        let recorder = Recorder::default();
        let mut fanout = Fanout::new();
        let mut policy = focused(UnfocusedBehavior::Freeze, t0);
        policy.set_shutter(true);
        fanout.add("a", policy, Box::new(recorder.clone()));
        fanout.on_frame(&frame(1, 200), t0);
        fanout.on_frame(&frame(2, 200), after(t0, 33));
        fanout.on_frame(&frame(3, 200), after(t0, 66));
        let sent = recorder.sent();
        assert_eq!(sent.len(), 1);
        assert_eq!(sent[0].0, Status::Shuttered);
        assert_eq!(sent[0].2, 16, "limited-range black");
    }

    #[test]
    fn closing_the_shutter_on_a_live_session_sends_black_at_once() {
        let t0 = Instant::now();
        let recorder = Recorder::default();
        let mut fanout = Fanout::new();
        fanout.add(
            "a",
            focused(UnfocusedBehavior::Freeze, t0),
            Box::new(recorder.clone()),
        );
        fanout.on_frame(&frame(1, 200), t0);
        fanout.policy_mut("a").unwrap().set_shutter(true);
        fanout.on_frame(&frame(2, 200), after(t0, 33));
        fanout.policy_mut("a").unwrap().set_shutter(false);
        fanout.on_frame(&frame(3, 200), after(t0, 66));
        let sent = recorder.sent();
        assert_eq!(sent.len(), 3);
        assert_eq!(sent[1].0, Status::Shuttered);
        assert_eq!(sent[2], (Status::Live, 3, 200));
    }

    #[test]
    fn a_shutter_keeps_the_camera_black_through_a_keepalive() {
        let t0 = Instant::now();
        let recorder = Recorder::default();
        let mut fanout = Fanout::new();
        let mut policy = unfocused(UnfocusedBehavior::KeepLive);
        policy.set_shutter(true);
        fanout.add("a", policy, Box::new(recorder.clone()));
        fanout.on_frame(&frame(1, 200), t0);
        fanout.on_frame(&frame(2, 200), after(t0, 1_000));
        assert_eq!(recorder.sent().len(), 2);
        assert!(recorder
            .sent()
            .iter()
            .all(|s| s.0 == Status::Shuttered && s.2 == 16));
    }

    #[test]
    fn sessions_are_independent() {
        let t0 = Instant::now();
        let (work, games) = (Recorder::default(), Recorder::default());
        let mut fanout = Fanout::new();
        fanout.add(
            "work",
            focused(UnfocusedBehavior::Freeze, t0),
            Box::new(work.clone()),
        );
        fanout.add(
            "games",
            unfocused(UnfocusedBehavior::Freeze),
            Box::new(games.clone()),
        );
        for n in 0..10u64 {
            fanout.on_frame(&frame(n, 50), after(t0, n * 33));
        }
        assert_eq!(work.sent().len(), 10);
        assert_eq!(games.sent().len(), 1);
    }

    #[test]
    fn adding_removing_and_replacing_sessions() {
        let t0 = Instant::now();
        let (first, second) = (Recorder::default(), Recorder::default());
        let mut fanout = Fanout::new();
        assert!(fanout.is_empty());
        fanout.add(
            "a",
            focused(UnfocusedBehavior::Freeze, t0),
            Box::new(first.clone()),
        );
        fanout.add(
            "a",
            focused(UnfocusedBehavior::Freeze, t0),
            Box::new(second.clone()),
        );
        assert_eq!(fanout.len(), 1);
        fanout.on_frame(&frame(1, 1), t0);
        assert!(first.sent().is_empty());
        assert_eq!(second.sent().len(), 1);
        assert!(fanout.remove("a"));
        assert!(!fanout.remove("a"));
        fanout.on_frame(&frame(2, 1), after(t0, 33));
        assert_eq!(second.sent().len(), 1);
    }

    #[test]
    fn while_the_camera_is_busy_sessions_are_kept_alive_with_the_busy_status() {
        let t0 = Instant::now();
        let recorder = Recorder::default();
        let mut fanout = Fanout::new();
        fanout.add(
            "a",
            focused(UnfocusedBehavior::Freeze, t0),
            Box::new(recorder.clone()),
        );
        fanout.on_frame(&frame(7, 90), t0);
        fanout.on_idle(Status::CameraBusy, SIZE, after(t0, 500));
        assert_eq!(recorder.sent().len(), 1, "not due yet");
        fanout.on_idle(Status::CameraBusy, SIZE, after(t0, 1_000));
        let sent = recorder.sent();
        assert_eq!(sent.len(), 2);
        assert_eq!(
            sent[1],
            (Status::CameraBusy, 7, 90),
            "the last picture, marked busy"
        );
    }

    #[test]
    fn a_session_that_never_got_a_frame_is_shown_black_while_the_camera_is_busy() {
        let t0 = Instant::now();
        let recorder = Recorder::default();
        let mut fanout = Fanout::new();
        fanout.add(
            "a",
            focused(UnfocusedBehavior::Freeze, t0),
            Box::new(recorder.clone()),
        );
        fanout.on_idle(Status::CameraBusy, SIZE, t0);
        assert_eq!(recorder.sent(), vec![(Status::CameraBusy, 0, 16)]);
    }
}
