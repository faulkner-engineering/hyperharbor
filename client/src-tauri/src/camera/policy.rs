//! What one session's virtual camera shows. A pure state machine: the caller passes the time, so
//! the grace period is tested without sleeping.
//!
//! Priority, highest first: the privacy shutter, then focus. A session starts unfocused. After
//! focus is lost the session keeps showing live video for `FOCUS_GRACE`, so passing through the
//! window while switching with Alt+Tab does not flicker the picture.

use serde::{Deserialize, Serialize};
use std::time::{Duration, Instant};

/// How long live video continues after the session window loses focus.
pub const FOCUS_GRACE: Duration = Duration::from_millis(300);

/// What an unfocused session shows. The default, a frozen frame, costs the least.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum UnfocusedBehavior {
    #[default]
    Freeze,
    Blur,
    KeepLive,
}

/// The frame source for one virtual camera right now.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FrameMode {
    /// The current physical frame.
    Live,
    /// The last live frame, repeated.
    Frozen,
    /// The current physical frame, blurred.
    Blurred,
    /// Black: the privacy shutter is closed.
    Shuttered,
}

#[derive(Debug, Clone)]
pub struct SessionPolicy {
    unfocused: UnfocusedBehavior,
    focused: bool,
    shutter: bool,
    focus_lost_at: Option<Instant>,
}

impl SessionPolicy {
    /// A new session is unfocused until the window system says otherwise.
    pub fn new(unfocused: UnfocusedBehavior) -> Self {
        Self {
            unfocused,
            focused: false,
            shutter: false,
            focus_lost_at: None,
        }
    }

    pub fn set_focused(&mut self, focused: bool, now: Instant) {
        if focused == self.focused {
            return;
        }
        self.focused = focused;
        self.focus_lost_at = if focused { None } else { Some(now) };
    }

    pub fn set_shutter(&mut self, closed: bool) {
        self.shutter = closed;
    }

    pub fn set_unfocused_behavior(&mut self, behavior: UnfocusedBehavior) {
        self.unfocused = behavior;
    }

    pub fn focused(&self) -> bool {
        self.focused
    }

    /// The mode at `now`.
    pub fn mode(&self, now: Instant) -> FrameMode {
        if self.shutter {
            return FrameMode::Shuttered;
        }
        if self.focused {
            return FrameMode::Live;
        }
        if let Some(lost) = self.focus_lost_at {
            if now.saturating_duration_since(lost) < FOCUS_GRACE {
                return FrameMode::Live;
            }
        }
        match self.unfocused {
            UnfocusedBehavior::Freeze => FrameMode::Frozen,
            UnfocusedBehavior::Blur => FrameMode::Blurred,
            UnfocusedBehavior::KeepLive => FrameMode::Live,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn later(base: Instant, ms: u64) -> Instant {
        base + Duration::from_millis(ms)
    }

    #[test]
    fn a_new_session_is_unfocused_and_follows_the_preference() {
        let now = Instant::now();
        assert_eq!(
            SessionPolicy::new(UnfocusedBehavior::Freeze).mode(now),
            FrameMode::Frozen
        );
        assert_eq!(
            SessionPolicy::new(UnfocusedBehavior::Blur).mode(now),
            FrameMode::Blurred
        );
        assert_eq!(
            SessionPolicy::new(UnfocusedBehavior::KeepLive).mode(now),
            FrameMode::Live
        );
    }

    #[test]
    fn a_focused_session_is_live_whatever_the_preference() {
        let now = Instant::now();
        for behavior in [
            UnfocusedBehavior::Freeze,
            UnfocusedBehavior::Blur,
            UnfocusedBehavior::KeepLive,
        ] {
            let mut policy = SessionPolicy::new(behavior);
            policy.set_focused(true, now);
            assert_eq!(policy.mode(now), FrameMode::Live);
            assert_eq!(policy.mode(later(now, 60_000)), FrameMode::Live);
        }
    }

    #[test]
    fn live_video_continues_through_the_grace_period_after_focus_is_lost() {
        let t0 = Instant::now();
        let mut policy = SessionPolicy::new(UnfocusedBehavior::Freeze);
        policy.set_focused(true, t0);
        policy.set_focused(false, t0);
        assert_eq!(policy.mode(t0), FrameMode::Live);
        assert_eq!(policy.mode(later(t0, 299)), FrameMode::Live);
        assert_eq!(policy.mode(later(t0, 300)), FrameMode::Frozen);
        assert_eq!(policy.mode(later(t0, 10_000)), FrameMode::Frozen);
    }

    #[test]
    fn regaining_focus_inside_the_grace_period_never_leaves_live() {
        let t0 = Instant::now();
        let mut policy = SessionPolicy::new(UnfocusedBehavior::Blur);
        policy.set_focused(true, t0);
        policy.set_focused(false, later(t0, 100));
        policy.set_focused(true, later(t0, 200));
        assert_eq!(policy.mode(later(t0, 200)), FrameMode::Live);
        assert_eq!(policy.mode(later(t0, 5_000)), FrameMode::Live);
    }

    #[test]
    fn the_grace_period_restarts_each_time_focus_is_lost() {
        let t0 = Instant::now();
        let mut policy = SessionPolicy::new(UnfocusedBehavior::Freeze);
        policy.set_focused(true, t0);
        policy.set_focused(false, t0);
        policy.set_focused(true, later(t0, 1_000));
        policy.set_focused(false, later(t0, 2_000));
        assert_eq!(policy.mode(later(t0, 2_100)), FrameMode::Live);
        assert_eq!(policy.mode(later(t0, 2_300)), FrameMode::Frozen);
    }

    #[test]
    fn repeating_the_same_focus_event_changes_nothing() {
        let t0 = Instant::now();
        let mut policy = SessionPolicy::new(UnfocusedBehavior::Freeze);
        policy.set_focused(true, t0);
        policy.set_focused(false, t0);
        // A repeated "lost" event much later must not restart the grace period.
        policy.set_focused(false, later(t0, 250));
        assert_eq!(policy.mode(later(t0, 300)), FrameMode::Frozen);
    }

    #[test]
    fn the_shutter_overrides_focus_and_grace() {
        let t0 = Instant::now();
        let mut policy = SessionPolicy::new(UnfocusedBehavior::KeepLive);
        policy.set_focused(true, t0);
        policy.set_shutter(true);
        assert_eq!(policy.mode(t0), FrameMode::Shuttered);
        policy.set_focused(false, t0);
        assert_eq!(policy.mode(t0), FrameMode::Shuttered);
        assert_eq!(policy.mode(later(t0, 5_000)), FrameMode::Shuttered);
    }

    #[test]
    fn opening_the_shutter_returns_to_what_focus_decides() {
        let t0 = Instant::now();
        let mut policy = SessionPolicy::new(UnfocusedBehavior::Blur);
        policy.set_shutter(true);
        policy.set_focused(true, t0);
        policy.set_shutter(false);
        assert_eq!(policy.mode(t0), FrameMode::Live);
        policy.set_focused(false, t0);
        assert_eq!(policy.mode(later(t0, 1_000)), FrameMode::Blurred);
    }

    #[test]
    fn changing_the_preference_applies_to_an_unfocused_session_at_once() {
        let t0 = Instant::now();
        let mut policy = SessionPolicy::new(UnfocusedBehavior::Freeze);
        assert_eq!(policy.mode(t0), FrameMode::Frozen);
        policy.set_unfocused_behavior(UnfocusedBehavior::Blur);
        assert_eq!(policy.mode(t0), FrameMode::Blurred);
        policy.set_unfocused_behavior(UnfocusedBehavior::KeepLive);
        assert_eq!(policy.mode(t0), FrameMode::Live);
    }

    #[test]
    fn a_clock_that_runs_backwards_does_not_panic() {
        let t0 = Instant::now();
        let mut policy = SessionPolicy::new(UnfocusedBehavior::Freeze);
        policy.set_focused(true, later(t0, 1_000));
        policy.set_focused(false, later(t0, 1_000));
        assert_eq!(policy.mode(t0), FrameMode::Live);
    }

    #[test]
    fn the_preference_serializes_as_camel_case_words() {
        assert_eq!(
            serde_json::to_string(&UnfocusedBehavior::KeepLive).unwrap(),
            "\"keepLive\""
        );
        assert_eq!(
            serde_json::from_str::<UnfocusedBehavior>("\"blur\"").unwrap(),
            UnfocusedBehavior::Blur
        );
    }
}
