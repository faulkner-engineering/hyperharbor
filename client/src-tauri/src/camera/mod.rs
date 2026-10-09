//! Camera sharing: one physical camera, one virtual camera per Remote Desktop session.
//!
//! mstsc gives the physical camera to the first session that asks for it. Each session instead
//! redirects its own virtual camera (a Windows 11 user-mode virtual camera whose frames the client
//! fans out from the single physical capture), so several sessions can show video at once.
//!
//! The pure parts live here and are tested without a camera: device naming, symbolic link
//! resolution, the per-session policy, and orphan reconciliation. The Windows parts sit behind
//! the traits in `resolve` and `reconcile`.

pub mod backend;
pub mod capture;
pub mod failure;
pub mod fanout;
pub mod frame;
pub mod ledger;
pub mod live;
pub mod mfapi;
pub mod naming;
pub mod pipe;
pub mod policy;
pub mod reconcile;
pub mod resolve;
pub mod service;
pub mod setup;
pub mod setup_windows;
pub mod support;
pub mod winmf;

/// What can go wrong with camera sharing. The messages reach the UI.
#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum CameraError {
    #[error("The virtual camera did not appear.")]
    DeviceNotFound,

    #[error("More than one camera has that name, so the right one cannot be chosen.")]
    AmbiguousDevice,

    #[error("The camera's device path is not valid.")]
    InvalidDeviceLink,

    #[error("The cameras could not be listed: {0}")]
    Enumeration(String),

    #[error("A virtual camera could not be removed: {0}")]
    Removal(String),

    #[error("The camera ledger could not be saved: {0}")]
    Ledger(String),
}

/// Which camera an .rdp file redirects.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CameraRedirect {
    /// No camera redirection.
    None,
    /// Every physical camera (`*`): mstsc gives it to the first session only.
    AllPhysical,
    /// Exactly one device, by its symbolic link: a session's own virtual camera.
    Link(String),
}

impl CameraRedirect {
    /// A redirect to one device. The link is checked here, so a value that could change other
    /// lines of the .rdp file cannot be built.
    pub fn link(link: impl Into<String>) -> Result<Self, CameraError> {
        let link = link.into();
        if resolve::is_valid_link(&link) {
            Ok(Self::Link(link))
        } else {
            Err(CameraError::InvalidDeviceLink)
        }
    }

    /// The `camerastoredirect` line, or None when nothing is redirected.
    pub fn rdp_line(&self) -> Option<String> {
        match self {
            Self::None => Option::None,
            Self::AllPhysical => Some("camerastoredirect:s:*".to_string()),
            Self::Link(link) => Some(format!("camerastoredirect:s:{link}")),
        }
    }
}
