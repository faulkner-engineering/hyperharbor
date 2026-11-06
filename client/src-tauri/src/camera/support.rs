//! Whether this device can share its camera. User-mode virtual cameras (`MFCreateVirtualCamera`)
//! need Windows 11, which HyperHarbor needs from 0.3.0. On a device without them (Windows 10, or a
//! Windows 11 edition without Media Foundation) the physical camera goes to the first session that
//! asks for it.

/// The first Windows 11 build.
pub const MIN_BUILD: u32 = 22000;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Support {
    /// Virtual cameras can be created.
    Available,
    /// No virtual cameras here, with the reason.
    Unavailable(String),
}

impl Support {
    /// Decides from the Windows build number and whether the virtual camera API answered that it
    /// supports software cameras. A build below Windows 11 never reaches the API check.
    pub fn from_probe(build: u32, api_supported: Result<bool, String>) -> Self {
        if build < MIN_BUILD {
            return Self::Unavailable(format!(
                "HyperHarbor needs Windows 11, and this device runs Windows build {build}."
            ));
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
            Self::Unavailable(reason) => Some(format!("Camera sharing is not available: {reason}")),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_before_windows_11_are_unavailable_and_say_why() {
        for build in [19045, MIN_BUILD - 1] {
            let support = Support::from_probe(build, Ok(true));
            assert!(!support.is_available());
            let message = support.message().unwrap();
            assert!(message.contains("needs Windows 11"), "{message}");
            assert!(message.contains(&format!("build {build}")), "{message}");
        }
    }

    #[test]
    fn the_first_windows_11_build_is_supported() {
        assert_eq!(Support::from_probe(MIN_BUILD, Ok(true)), Support::Available);
        assert_eq!(Support::from_probe(26200, Ok(true)), Support::Available);
    }

    #[test]
    fn builds_before_windows_11_never_ask_the_api() {
        // The probe result is not even consulted below the minimum build.
        let support = Support::from_probe(19045, Err("missing".into()));
        assert!(support.message().unwrap().contains("needs Windows 11"));
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
        assert!(Support::Available.message().is_none());
    }
}
