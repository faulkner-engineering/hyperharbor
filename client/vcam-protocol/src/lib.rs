//! The wire format between the client (which captures the physical camera) and the virtual
//! camera source (a DLL the Windows Frame Server loads, one instance per virtual camera).
//!
//! The client serves a named pipe per virtual camera and writes frames to it; the source
//! connects and keeps the newest one. A pipe needs no special privilege in the Frame Server's
//! session (a named file mapping in the Global namespace does), reports a vanished client at
//! once, and lets the source check who is serving it.
//!
//! Every message is a fixed 32-byte header followed by `payload_len` bytes of NV12.

pub mod nv12;

/// The COM class of the virtual camera source. The client passes it to `MFCreateVirtualCamera`
/// and the DLL registers itself under it, so both must be the same value.
pub const SOURCE_CLSID: u128 = 0xb5e8_c0a2_7d3f_4b6e_9a41_2f5c_8d1e_6a70;

/// `SOURCE_CLSID` in the registry's braced form.
pub const SOURCE_CLSID_STRING: &str = "{B5E8C0A2-7D3F-4B6E-9A41-2F5C8D1E6A70}";

/// "HHVC" read as little-endian bytes.
pub const MAGIC: u32 = 0x4356_4848;
pub const VERSION: u16 = 1;
pub const HEADER_LEN: usize = 32;

/// The largest payload accepted: 4K NV12 is 12.4 MB, so this is generous and still bounded.
pub const MAX_PAYLOAD: u32 = 16 * 1024 * 1024;

/// What a frame holds. Only NV12 exists today; the field lets a later version add formats.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum PixelFormat {
    Nv12 = 1,
}

/// What the client says about the picture, in addition to its pixels.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum Status {
    /// A normal frame.
    Live = 0,
    /// The privacy shutter is closed; the frame is black.
    Shuttered = 1,
    /// The physical camera is held by another application; the frame is the last one or black.
    CameraBusy = 2,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FrameHeader {
    pub width: u32,
    pub height: u32,
    pub format: PixelFormat,
    pub status: Status,
    /// Counts up per captured frame on the client.
    pub sequence: u64,
    pub payload_len: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DecodeError {
    BadMagic,
    UnsupportedVersion,
    UnknownFormat,
    UnknownStatus,
    /// Odd or zero dimensions, or a payload that is not one frame of the stated size.
    BadGeometry,
    PayloadTooLarge,
}

/// Bytes of one NV12 frame, or None for sizes NV12 cannot represent.
pub fn nv12_len(width: u32, height: u32) -> Option<u32> {
    if width < 2 || height < 2 || !width.is_multiple_of(2) || !height.is_multiple_of(2) {
        return None;
    }
    width.checked_mul(height)?.checked_mul(3)?.checked_div(2)
}

impl FrameHeader {
    pub fn encode(&self) -> [u8; HEADER_LEN] {
        let mut out = [0u8; HEADER_LEN];
        out[0..4].copy_from_slice(&MAGIC.to_le_bytes());
        out[4..6].copy_from_slice(&VERSION.to_le_bytes());
        out[6..8].copy_from_slice(&(self.format as u16).to_le_bytes());
        out[8..12].copy_from_slice(&self.width.to_le_bytes());
        out[12..16].copy_from_slice(&self.height.to_le_bytes());
        out[16..18].copy_from_slice(&(self.status as u16).to_le_bytes());
        // Bytes 18 and 19 are reserved.
        out[20..28].copy_from_slice(&self.sequence.to_le_bytes());
        out[28..32].copy_from_slice(&self.payload_len.to_le_bytes());
        out
    }

    /// Reads and validates a header. A header that passes describes exactly one frame, so the
    /// caller can allocate `payload_len` bytes without further checks.
    pub fn decode(bytes: &[u8; HEADER_LEN]) -> Result<Self, DecodeError> {
        let u16_at = |i: usize| u16::from_le_bytes([bytes[i], bytes[i + 1]]);
        let u32_at =
            |i: usize| u32::from_le_bytes([bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]]);
        if u32_at(0) != MAGIC {
            return Err(DecodeError::BadMagic);
        }
        if u16_at(4) != VERSION {
            return Err(DecodeError::UnsupportedVersion);
        }
        let format = match u16_at(6) {
            1 => PixelFormat::Nv12,
            _ => return Err(DecodeError::UnknownFormat),
        };
        let status = match u16_at(16) {
            0 => Status::Live,
            1 => Status::Shuttered,
            2 => Status::CameraBusy,
            _ => return Err(DecodeError::UnknownStatus),
        };
        let (width, height, payload_len) = (u32_at(8), u32_at(12), u32_at(28));
        if payload_len > MAX_PAYLOAD {
            return Err(DecodeError::PayloadTooLarge);
        }
        if nv12_len(width, height) != Some(payload_len) {
            return Err(DecodeError::BadGeometry);
        }
        let mut sequence = [0u8; 8];
        sequence.copy_from_slice(&bytes[20..28]);
        Ok(Self {
            width,
            height,
            format,
            status,
            sequence: u64::from_le_bytes(sequence),
            payload_len,
        })
    }
}

/// FNV-1a over the bytes: stable, dependency-free, and the same in both processes. It names pipes;
/// it is not a security measure (the pipe's access list and the server check are).
fn fnv1a(bytes: &[u8]) -> u64 {
    bytes.iter().fold(0xcbf2_9ce4_8422_2325, |hash, &b| {
        (hash ^ u64::from(b)).wrapping_mul(0x0100_0000_01b3)
    })
}

/// The pipe a virtual camera's frames travel on. The Frame Server tells the source the camera's
/// friendly name and the process that created it, which is all both sides need to agree on a
/// name. Including the creator's process id means a second client instance, or a stale pipe from a
/// crashed one, never shares a name with a live camera.
pub fn pipe_name(creator_pid: u32, friendly_name: &str) -> String {
    format!(
        r"\\.\pipe\HyperHarbor.VCam.{creator_pid}.{:016x}",
        fnv1a(friendly_name.as_bytes())
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn header() -> FrameHeader {
        FrameHeader {
            width: 1280,
            height: 720,
            format: PixelFormat::Nv12,
            status: Status::Live,
            sequence: 42,
            payload_len: 1_382_400,
        }
    }

    #[test]
    fn a_header_round_trips() {
        let h = header();
        assert_eq!(FrameHeader::decode(&h.encode()), Ok(h));
        let shuttered = FrameHeader {
            status: Status::Shuttered,
            ..h
        };
        assert_eq!(FrameHeader::decode(&shuttered.encode()), Ok(shuttered));
    }

    #[test]
    fn the_layout_is_fixed() {
        let bytes = header().encode();
        assert_eq!(&bytes[0..4], b"HHVC");
        assert_eq!(bytes.len(), 32);
        assert_eq!(u32::from_le_bytes(bytes[8..12].try_into().unwrap()), 1280);
        assert_eq!(
            u32::from_le_bytes(bytes[28..32].try_into().unwrap()),
            1_382_400
        );
    }

    #[test]
    fn nv12_sizes_match_the_frame_server() {
        assert_eq!(nv12_len(1280, 720), Some(1_382_400));
        assert_eq!(nv12_len(640, 480), Some(460_800));
        assert_eq!(nv12_len(641, 480), None);
        assert_eq!(nv12_len(0, 0), None);
        assert_eq!(nv12_len(u32::MAX - 1, u32::MAX - 1), None);
    }

    #[test]
    fn damaged_headers_are_refused() {
        let good = header().encode();
        let mut bad = good;
        bad[0] ^= 1;
        assert_eq!(FrameHeader::decode(&bad), Err(DecodeError::BadMagic));
        let mut bad = good;
        bad[4] = 9;
        assert_eq!(
            FrameHeader::decode(&bad),
            Err(DecodeError::UnsupportedVersion)
        );
        let mut bad = good;
        bad[6] = 7;
        assert_eq!(FrameHeader::decode(&bad), Err(DecodeError::UnknownFormat));
        let mut bad = good;
        bad[16] = 9;
        assert_eq!(FrameHeader::decode(&bad), Err(DecodeError::UnknownStatus));
    }

    #[test]
    fn a_payload_length_that_is_not_one_frame_is_refused() {
        let mut h = header();
        h.payload_len -= 2;
        assert_eq!(
            FrameHeader::decode(&h.encode()),
            Err(DecodeError::BadGeometry)
        );
        let mut h = header();
        h.width = 1281;
        assert_eq!(
            FrameHeader::decode(&h.encode()),
            Err(DecodeError::BadGeometry)
        );
    }

    #[test]
    fn an_oversized_frame_is_refused_before_anything_is_allocated() {
        let h = FrameHeader {
            width: 8192,
            height: 8192,
            payload_len: nv12_len(8192, 8192).unwrap(),
            ..header()
        };
        assert_eq!(
            FrameHeader::decode(&h.encode()),
            Err(DecodeError::PayloadTooLarge)
        );
    }

    #[test]
    fn pipe_names_are_stable_and_distinguish_cameras_and_creators() {
        let a = pipe_name(100, "HyperHarbor Camera (Work)");
        assert_eq!(a, pipe_name(100, "HyperHarbor Camera (Work)"));
        assert_ne!(a, pipe_name(101, "HyperHarbor Camera (Work)"));
        assert_ne!(a, pipe_name(100, "HyperHarbor Camera (Games)"));
        assert!(a.starts_with(r"\\.\pipe\HyperHarbor.VCam.100."));
    }

    #[test]
    fn pipe_names_contain_no_characters_from_the_friendly_name() {
        let name = pipe_name(1, "evil\\..\\name\r\n");
        assert!(!name[r"\\.\pipe\".len()..].contains('\\'));
        assert!(name.is_ascii());
    }
}
