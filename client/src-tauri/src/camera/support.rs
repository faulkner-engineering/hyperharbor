//! Whether this device can share its camera. User-mode virtual cameras (`MFCreateVirtualCamera`)
//! arrived in Windows 11, so Windows 10 keeps the old behaviour: the physical camera goes to the
//! first session that asks for it.

/// The first Windows 11 build number.
pub const MIN_BUILD: u32 = 22000;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Support {
    /// Virtual cameras can be created.
    Available,
    /// Windows 10 or older: only the physical camera, for one VM at a time.
    NeedsWindows11 { build: u32 },
    /// Windows 11, but the API reports no support (or could not be loaded).
    Unavailable(String),
}

impl Support {
    /// Decides from the Windows build number and whether the virtual camera API answered that it
    /// supports software cameras. A build below Windows 11 never reaches the API check.
    pub fn from_probe(build: u32, api_supported: Result<bool, String>) -> Self {
        if build < MIN_BUILD {
            return Self::NeedsWindows11 { build };
        }
        match api_supported {
            Ok(true) => Self::Available,
            Ok(false) => {
                Self::Unavailable("Windows reports no support for virtual cameras.".into())
            }
            Err(reason) => Self::Unavailable(reason),
        }
    }

    pub fn is_available(&self) -> bool {
        matches!(self, Self::Available)
    }

    /// What the UI says when sharing is not possible; None when it is.
    pub fn message(&self) -> Option<String> {
        match self {
            Self::Available => None,
            Self::NeedsWindows11 { .. } => Some(
                "Camera sharing requires Windows 11 on this device. Only one VM at a time can use the camera here."
                    .to_string(),
            ),
            Self::Unavailable(reason) => Some(format!("Camera sharing is not available: {reason}")),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn windows_10_builds_need_windows_11() {
        assert_eq!(
            Support::from_probe(19045, Ok(true)),
            Support::NeedsWindows11 { build: 19045 }
        );
        assert_eq!(
            Support::from_probe(MIN_BUILD - 1, Ok(true)),
            Support::NeedsWindows11 {
                build: MIN_BUILD - 1
            }
        );
    }

    #[test]
    fn the_first_windows_11_build_is_supported() {
        assert_eq!(Support::from_probe(MIN_BUILD, Ok(true)), Support::Available);
        assert_eq!(Support::from_probe(26200, Ok(true)), Support::Available);
    }

    #[test]
    fn windows_10_never_asks_the_api() {
        // The probe result is not even consulted below the minimum build.
        assert_eq!(
            Support::from_probe(19045, Err("missing".into())),
            Support::NeedsWindows11 { build: 19045 }
        );
    }

    #[test]
    fn a_windows_11_device_without_the_api_is_unavailable_with_a_reason() {
        assert!(matches!(
            Support::from_probe(26200, Ok(false)),
            Support::Unavailable(_)
        ));
        let missing = Support::from_probe(
            26200,
            Err("mfsensorgroup.dll has no MFCreateVirtualCamera".into()),
        );
        assert!(missing.message().unwrap().contains("MFCreateVirtualCamera"));
    }

    #[test]
    fn the_windows_10_message_names_the_requirement_and_the_fallback() {
        let message = Support::NeedsWindows11 { build: 19045 }.message().unwrap();
        assert!(message.contains("requires Windows 11"));
        assert!(message.contains("one VM at a time"));
        assert!(Support::Available.message().is_none());
    }
}
