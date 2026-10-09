//! Finds the symbolic link of a created virtual camera. The .rdp file names the device by that
//! link (`camerastoredirect:s:<link>`), so each session redirects exactly its own camera.

use super::{naming, CameraError};
use std::time::{Duration, Instant};

/// A video capture device as Media Foundation lists it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VideoDevice {
    pub name: String,
    pub symbolic_link: String,
}

/// Lists the video capture devices. The Windows implementation enumerates Media Foundation
/// sources of the video capture type; tests use a fake.
pub trait DeviceEnumerator {
    fn video_devices(&self) -> Result<Vec<VideoDevice>, CameraError>;
}

/// True when `link` is safe to write into an .rdp file: a device interface path, with nothing
/// that could end the line or split the value (mstsc separates several links with ';').
pub fn is_valid_link(link: &str) -> bool {
    link.starts_with(r"\\?\")
        && link.len() <= 1024
        && !link
            .chars()
            .any(|c| c.is_control() || c == ';' || c.is_whitespace())
}

/// The symbolic link of the device created as `created_name`. Fails when no device matches, when
/// several do (the choice would be a guess), or when the link is not safe to write to the file.
pub fn resolve_symbolic_link(
    devices: &[VideoDevice],
    created_name: &str,
) -> Result<String, CameraError> {
    let mut matches = devices
        .iter()
        .filter(|device| naming::names_match(&device.name, created_name));
    let first = matches.next().ok_or(CameraError::DeviceNotFound)?;
    if matches.next().is_some() {
        return Err(CameraError::AmbiguousDevice);
    }
    if !is_valid_link(&first.symbolic_link) {
        return Err(CameraError::InvalidDeviceLink);
    }
    Ok(first.symbolic_link.clone())
}

/// Polls until the device created as `created_name` is listed, then returns its link. The device
/// shows up within milliseconds on the build tested, but it is created by another process, so the
/// wait is bounded rather than assumed.
pub fn wait_for_symbolic_link(
    enumerator: &dyn DeviceEnumerator,
    created_name: &str,
    timeout: Duration,
    poll: Duration,
) -> Result<String, CameraError> {
    let started = Instant::now();
    loop {
        match resolve_symbolic_link(&enumerator.video_devices()?, created_name) {
            Err(CameraError::DeviceNotFound) if started.elapsed() < timeout => {
                std::thread::sleep(poll);
            }
            other => return other,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    const PHYSICAL: &str = r"\\?\usb#vid_046d&pid_082d&mi_00#7&17e9de75&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";
    const WORK: &str = r"\\?\swd#vcamdevapi#aaaa#{e5323777-f976-4f5b-9b55-b94699c46e44}\{11111111-1111-1111-1111-111111111111}";
    const GAMES: &str = r"\\?\swd#vcamdevapi#bbbb#{e5323777-f976-4f5b-9b55-b94699c46e44}\{22222222-2222-2222-2222-222222222222}";

    fn device(name: &str, link: &str) -> VideoDevice {
        VideoDevice {
            name: name.to_string(),
            symbolic_link: link.to_string(),
        }
    }

    fn listing() -> Vec<VideoDevice> {
        vec![
            device("HD Pro Webcam C920", PHYSICAL),
            device("HyperHarbor Camera (Work) (Windows Virtual Camera)", WORK),
            device("HyperHarbor Camera (Games) (Windows Virtual Camera)", GAMES),
        ]
    }

    #[test]
    fn each_session_gets_its_own_cameras_link() {
        assert_eq!(
            resolve_symbolic_link(&listing(), "HyperHarbor Camera (Work)").unwrap(),
            WORK
        );
        assert_eq!(
            resolve_symbolic_link(&listing(), "HyperHarbor Camera (Games)").unwrap(),
            GAMES
        );
    }

    #[test]
    fn the_physical_camera_is_never_chosen() {
        let link = resolve_symbolic_link(&listing(), "HyperHarbor Camera (Work)").unwrap();
        assert_ne!(link, PHYSICAL);
    }

    #[test]
    fn a_numbered_name_does_not_match_the_plain_one() {
        let devices = vec![device(
            "HyperHarbor Camera (Work #2) (Windows Virtual Camera)",
            GAMES,
        )];
        assert_eq!(
            resolve_symbolic_link(&devices, "HyperHarbor Camera (Work)"),
            Err(CameraError::DeviceNotFound)
        );
    }

    #[test]
    fn a_missing_device_is_reported() {
        assert_eq!(
            resolve_symbolic_link(&listing(), "HyperHarbor Camera (Nope)"),
            Err(CameraError::DeviceNotFound)
        );
        assert_eq!(
            resolve_symbolic_link(&[], "HyperHarbor Camera (Work)"),
            Err(CameraError::DeviceNotFound)
        );
    }

    #[test]
    fn two_devices_with_one_name_are_not_guessed_between() {
        let mut devices = listing();
        devices.push(device("HyperHarbor Camera (Work)", GAMES));
        assert_eq!(
            resolve_symbolic_link(&devices, "HyperHarbor Camera (Work)"),
            Err(CameraError::AmbiguousDevice)
        );
    }

    #[test]
    fn links_that_could_alter_the_rdp_file_are_refused() {
        for bad in [
            "",
            "plain",
            "\\\\?\\swd#x\r\ndrivestoredirect:s:*",
            "\\\\?\\swd#x\ndrivestoredirect:s:*",
            "\\\\?\\swd#x;\\\\?\\usb#y",
            "\\\\?\\swd#x y",
        ] {
            let devices = vec![device("HyperHarbor Camera (Work)", bad)];
            assert_eq!(
                resolve_symbolic_link(&devices, "HyperHarbor Camera (Work)"),
                Err(CameraError::InvalidDeviceLink),
                "{bad:?}"
            );
        }
        assert!(is_valid_link(WORK));
        assert!(is_valid_link(PHYSICAL));
    }

    struct Appears {
        after_calls: u32,
        calls: Cell<u32>,
    }

    impl DeviceEnumerator for Appears {
        fn video_devices(&self) -> Result<Vec<VideoDevice>, CameraError> {
            self.calls.set(self.calls.get() + 1);
            if self.calls.get() > self.after_calls {
                Ok(listing())
            } else {
                Ok(vec![device("HD Pro Webcam C920", PHYSICAL)])
            }
        }
    }

    #[test]
    fn waiting_returns_the_link_once_the_device_appears() {
        let enumerator = Appears {
            after_calls: 3,
            calls: Cell::new(0),
        };
        let link = wait_for_symbolic_link(
            &enumerator,
            "HyperHarbor Camera (Work)",
            Duration::from_secs(5),
            Duration::from_millis(1),
        )
        .unwrap();
        assert_eq!(link, WORK);
        assert_eq!(enumerator.calls.get(), 4);
    }

    #[test]
    fn waiting_gives_up_after_the_timeout() {
        let enumerator = Appears {
            after_calls: u32::MAX,
            calls: Cell::new(0),
        };
        let result = wait_for_symbolic_link(
            &enumerator,
            "HyperHarbor Camera (Work)",
            Duration::from_millis(30),
            Duration::from_millis(5),
        );
        assert_eq!(result, Err(CameraError::DeviceNotFound));
    }

    struct Failing;
    impl DeviceEnumerator for Failing {
        fn video_devices(&self) -> Result<Vec<VideoDevice>, CameraError> {
            Err(CameraError::Enumeration("no Media Foundation".into()))
        }
    }

    #[test]
    fn an_enumeration_failure_is_not_retried_as_a_missing_device() {
        let result = wait_for_symbolic_link(
            &Failing,
            "HyperHarbor Camera (Work)",
            Duration::from_secs(5),
            Duration::from_millis(1),
        );
        assert!(matches!(result, Err(CameraError::Enumeration(_))));
    }
}
