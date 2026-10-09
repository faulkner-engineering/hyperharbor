//! Why the physical camera could not be used, from the HRESULT Media Foundation reported.
//!
//! The constants are Media Foundation's (checked against the `windows` crate's definitions). A
//! camera held by an application that uses it exclusively reports "preempted" while streaming or
//! "failed to start streaming" when it is opened; both mean "someone else has it". Most modern
//! applications share the camera through the Windows Frame Server and cause neither.

/// What the UI says about the physical camera.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CameraFailure {
    /// Another application holds the camera.
    InUse,
    /// The camera was unplugged or disabled.
    Gone,
    /// Windows' camera privacy setting blocks this app.
    PrivacyBlocked,
    /// There is no camera.
    NoCamera,
    /// Anything else.
    Other,
}

pub const MF_E_HW_MFT_FAILED_START_STREAMING: u32 = 0xC00D_3704;
pub const MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED: u32 = 0xC00D_3EA2;
pub const MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED: u32 = 0xC00D_3EA3;
pub const MF_E_NO_CAPTURE_DEVICES_AVAILABLE: u32 = 0xC00D_ABE0;
pub const E_ACCESSDENIED: u32 = 0x8007_0005;
/// HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION) and ERROR_BUSY, which some drivers return directly.
pub const SHARING_VIOLATION: u32 = 0x8007_0020;
pub const ERROR_BUSY: u32 = 0x8007_00AA;

pub fn classify(hresult: u32) -> CameraFailure {
    match hresult {
        MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED
        | MF_E_HW_MFT_FAILED_START_STREAMING
        | SHARING_VIOLATION
        | ERROR_BUSY => CameraFailure::InUse,
        MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED => CameraFailure::Gone,
        MF_E_NO_CAPTURE_DEVICES_AVAILABLE => CameraFailure::NoCamera,
        E_ACCESSDENIED => CameraFailure::PrivacyBlocked,
        _ => CameraFailure::Other,
    }
}

impl CameraFailure {
    /// The line the UI shows.
    pub fn message(self) -> &'static str {
        match self {
            Self::InUse => "Another app is using your camera. Close it to share the camera with your VMs.",
            Self::Gone => "The camera was disconnected.",
            Self::PrivacyBlocked => {
                "Windows is blocking camera access for desktop apps. Turn it on in Settings > Privacy & security > Camera."
            }
            Self::NoCamera => "No camera was found on this device.",
            Self::Other => "The camera could not be opened.",
        }
    }

    /// True when waiting and trying again can help (the other app may close, the camera may come
    /// back). A privacy block or a missing camera is retried less often by the caller.
    pub fn is_transient(self) -> bool {
        matches!(self, Self::InUse | Self::Gone | Self::Other)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exclusive_use_by_another_app_is_in_use() {
        for code in [
            MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED,
            MF_E_HW_MFT_FAILED_START_STREAMING,
            SHARING_VIOLATION,
            ERROR_BUSY,
        ] {
            assert_eq!(classify(code), CameraFailure::InUse, "{code:#010X}");
        }
    }

    #[test]
    fn the_other_known_codes_map_to_their_causes() {
        assert_eq!(
            classify(MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED),
            CameraFailure::Gone
        );
        assert_eq!(
            classify(MF_E_NO_CAPTURE_DEVICES_AVAILABLE),
            CameraFailure::NoCamera
        );
        assert_eq!(classify(E_ACCESSDENIED), CameraFailure::PrivacyBlocked);
    }

    #[test]
    fn an_unknown_code_is_other_and_retryable() {
        assert_eq!(classify(0x8000_4005), CameraFailure::Other);
        assert!(CameraFailure::Other.is_transient());
    }

    #[test]
    fn privacy_blocks_and_missing_cameras_are_not_retried_eagerly() {
        assert!(!CameraFailure::PrivacyBlocked.is_transient());
        assert!(!CameraFailure::NoCamera.is_transient());
        assert!(CameraFailure::InUse.is_transient());
    }

    #[test]
    fn the_in_use_message_names_the_situation() {
        assert!(CameraFailure::InUse
            .message()
            .starts_with("Another app is using your camera"));
        assert!(CameraFailure::PrivacyBlocked.message().contains("Privacy"));
    }

    #[test]
    fn the_codes_match_the_windows_crate() {
        use windows::Win32::Media::MediaFoundation as mf;
        assert_eq!(
            MF_E_HW_MFT_FAILED_START_STREAMING,
            mf::MF_E_HW_MFT_FAILED_START_STREAMING.0 as u32
        );
        assert_eq!(
            MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED,
            mf::MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED.0 as u32
        );
        assert_eq!(
            MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED,
            mf::MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED.0 as u32
        );
        assert_eq!(
            MF_E_NO_CAPTURE_DEVICES_AVAILABLE,
            mf::MF_E_NO_CAPTURE_DEVICES_AVAILABLE.0 as u32
        );
    }
}
