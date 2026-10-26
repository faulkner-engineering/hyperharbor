//! The administrator passphrase card: a small window that stays on top of every other window, a full-screen Remote
//! Desktop session included, so the user can read the passphrase while typing it into the VM's UAC prompt. It never
//! takes the keyboard focus (the Remote Desktop window keeps it) and has no taskbar button. The passphrase is kept
//! here only while the card shows, never on disk or the clipboard, and is zeroed when it is dropped.

use serde::{Deserialize, Serialize};
use std::sync::Mutex;
use zeroize::Zeroize;

/// The window label; capabilities/default.json lists it for the drag permission.
pub const LABEL: &str = "guest-admin-overlay";
/// The route the card's window loads.
pub const ROUTE: &str = "overlay";
/// Logical size of the card.
pub const WIDTH: f64 = 360.0;
pub const HEIGHT: f64 = 104.0;
/// Logical distance from the monitor's right edge. Remote Desktop's connection bar sits at the top center.
const MARGIN: f64 = 24.0;
/// Logical distance from the top: below a maximized window's title bar, so the card never covers its close button.
const TOP: f64 = 64.0;

/// What the card shows.
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OverlayCard {
    pub vm_name: String,
    pub account_name: String,
    pub passphrase: String,
    pub expires_at: String,
}

impl std::fmt::Debug for OverlayCard {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OverlayCard")
            .field("vm_name", &self.vm_name)
            .field("account_name", &self.account_name)
            .field("expires_at", &self.expires_at)
            .finish_non_exhaustive()
    }
}

impl Drop for OverlayCard {
    fn drop(&mut self) {
        self.passphrase.zeroize();
    }
}

/// The card being shown, if any.
#[derive(Default)]
pub struct OverlayState {
    card: Mutex<Option<OverlayCard>>,
}

impl OverlayState {
    pub fn show(&self, card: OverlayCard) {
        *self.card.lock().unwrap() = Some(card);
    }

    pub fn current(&self) -> Option<OverlayCard> {
        self.card.lock().unwrap().clone()
    }

    /// Forgets the card; the passphrase is zeroed as it drops.
    pub fn clear(&self) {
        self.card.lock().unwrap().take();
    }
}

/// Where the card goes, in physical pixels: near the monitor's top-right corner, below title bar height.
pub fn position(monitor_x: i32, monitor_y: i32, monitor_width: u32, scale: f64) -> (i32, i32) {
    let width = (WIDTH * scale).round() as i32;
    let margin = (MARGIN * scale).round() as i32;
    let top = (TOP * scale).round() as i32;
    (
        monitor_x + monitor_width as i32 - width - margin,
        monitor_y + top,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn card() -> OverlayCard {
        OverlayCard {
            vm_name: "Dev Box".into(),
            account_name: "hha-owner".into(),
            passphrase: "Maple-River-Orbit-7".into(),
            expires_at: "2026-10-25T12:05:00Z".into(),
        }
    }

    #[test]
    fn debug_never_shows_the_passphrase() {
        let debug = format!("{:?}", card());
        assert!(!debug.contains("Maple"), "{debug}");
        assert!(debug.contains("hha-owner"));
    }

    #[test]
    fn the_card_is_kept_until_cleared() {
        let state = OverlayState::default();
        assert!(state.current().is_none());

        state.show(card());
        assert_eq!(state.current().unwrap().passphrase, "Maple-River-Orbit-7");

        state.clear();
        assert!(state.current().is_none());
    }

    #[test]
    fn the_card_sits_in_the_top_right_corner_of_its_monitor() {
        assert_eq!(position(0, 0, 1920, 1.0), (1920 - 360 - 24, 64));
        // A second monitor to the left, at 150 percent.
        assert_eq!(
            position(-2560, -200, 2560, 1.5),
            (-2560 + 2560 - 540 - 36, -200 + 96)
        );
    }
}
