//! NV12 frames and the few operations the fan-out needs: a black frame for the privacy shutter,
//! a blur for unfocused sessions, and a scale for cameras that do not offer the wanted size.
//!
//! NV12 is a full-resolution Y plane followed by one interleaved UV plane at half resolution in
//! both directions, so a frame of `width x height` is `width * height * 3 / 2` bytes. Sizes must be
//! even in both directions.

use hyperharbor_vcam_protocol::nv12;
use std::sync::Arc;

/// A frame size. Width and height are even, because the chroma plane is half resolution.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FrameSize {
    pub width: u32,
    pub height: u32,
}

impl FrameSize {
    /// The size offered first: 1280x720.
    pub const HD: Self = Self::new(1280, 720);
    /// The fallback for cameras that cannot deliver 720p: 640x480.
    pub const VGA: Self = Self::new(640, 480);

    pub const fn new(width: u32, height: u32) -> Self {
        Self { width, height }
    }

    /// True for sizes NV12 can represent.
    pub fn is_valid(&self) -> bool {
        self.width >= 2
            && self.height >= 2
            && self.width.is_multiple_of(2)
            && self.height.is_multiple_of(2)
    }

    /// Bytes in one NV12 frame of this size.
    pub fn nv12_len(&self) -> usize {
        self.width as usize * self.height as usize * 3 / 2
    }

    fn luma_len(&self) -> usize {
        self.width as usize * self.height as usize
    }
}

/// One NV12 frame, shared between the capture thread and the sessions' writers without copying.
#[derive(Debug, Clone)]
pub struct Frame {
    pub size: FrameSize,
    pub data: Arc<[u8]>,
    /// Counts up for every captured frame, so a writer can tell a new frame from a repeat.
    pub sequence: u64,
}

impl Frame {
    /// A frame, or None when `data` is not exactly one NV12 frame of `size`.
    pub fn new(size: FrameSize, data: Vec<u8>, sequence: u64) -> Option<Self> {
        (size.is_valid() && data.len() == size.nv12_len()).then(|| Self {
            size,
            data: data.into(),
            sequence,
        })
    }
}

/// Black in limited-range NV12: luma 16, neutral chroma 128.
pub fn black(size: FrameSize) -> Vec<u8> {
    nv12::black(size.width, size.height)
}

/// Blurs a frame with a box filter of the given radius on each plane. A radius of 0 copies the
/// frame. The sliding window makes the cost independent of the radius.
pub fn blur(src: &[u8], size: FrameSize, radius: usize) -> Vec<u8> {
    debug_assert_eq!(src.len(), size.nv12_len());
    let mut out = src.to_vec();
    if radius == 0 {
        return out;
    }
    let (w, h) = (size.width as usize, size.height as usize);
    let (luma, chroma) = out.split_at_mut(size.luma_len());
    blur_plane(&src[..size.luma_len()], luma, w, h, 1, radius);
    // The chroma plane interleaves U and V, so each channel is blurred with a stride of 2 and
    // half the radius (the plane is half resolution, so the picture blurs by the same amount).
    let chroma_radius = (radius / 2).max(1);
    let source_chroma = &src[size.luma_len()..];
    for channel in 0..2 {
        blur_plane_strided(
            source_chroma,
            chroma,
            w / 2,
            h / 2,
            2,
            channel,
            chroma_radius,
        );
    }
    out
}

fn blur_plane(src: &[u8], dst: &mut [u8], w: usize, h: usize, stride: usize, radius: usize) {
    blur_plane_strided(src, dst, w, h, stride, 0, radius);
}

/// Separable box blur of one channel of a plane: `w x h` samples, `stride` bytes apart within a
/// row, starting at byte `channel`. Edges use the samples that exist.
fn blur_plane_strided(
    src: &[u8],
    dst: &mut [u8],
    w: usize,
    h: usize,
    stride: usize,
    channel: usize,
    radius: usize,
) {
    let row_bytes = w * stride;
    let mut horizontal = vec![0u8; w * h];
    for y in 0..h {
        let row = &src[y * row_bytes..(y + 1) * row_bytes];
        slide(
            |x| row[x * stride + channel],
            w,
            radius,
            |x, value| horizontal[y * w + x] = value,
        );
    }
    for x in 0..w {
        slide(
            |y| horizontal[y * w + x],
            h,
            radius,
            |y, value| dst[y * row_bytes + x * stride + channel] = value,
        );
    }
}

/// A moving average over `len` samples with the window clipped at the ends.
fn slide(get: impl Fn(usize) -> u8, len: usize, radius: usize, mut set: impl FnMut(usize, u8)) {
    let mut sum: u32 = (0..=radius.min(len - 1)).map(|i| u32::from(get(i))).sum();
    let mut count = radius.min(len - 1) as u32 + 1;
    for i in 0..len {
        set(i, ((sum + count / 2) / count) as u8);
        let leaving = i.checked_sub(radius);
        let entering = i + radius + 1;
        if entering < len {
            sum += u32::from(get(entering));
            count += 1;
        }
        if let Some(leaving) = leaving {
            sum -= u32::from(get(leaving));
            count -= 1;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SMALL: FrameSize = FrameSize::new(16, 8);

    fn flat(size: FrameSize, y: u8, u: u8, v: u8) -> Vec<u8> {
        let mut data = vec![y; size.luma_len()];
        for _ in 0..size.luma_len() / 4 {
            data.push(u);
            data.push(v);
        }
        data
    }

    #[test]
    fn sizes_match_the_documented_formats() {
        assert_eq!(FrameSize::HD.nv12_len(), 1_382_400); // measured from the Frame Server
        assert_eq!(FrameSize::VGA.nv12_len(), 460_800);
        assert!(FrameSize::HD.is_valid());
        assert!(!FrameSize::new(15, 8).is_valid());
        assert!(!FrameSize::new(16, 7).is_valid());
        assert!(!FrameSize::new(0, 0).is_valid());
    }

    #[test]
    fn a_frame_must_be_exactly_one_nv12_frame() {
        assert!(Frame::new(SMALL, vec![0; SMALL.nv12_len()], 1).is_some());
        assert!(Frame::new(SMALL, vec![0; SMALL.nv12_len() - 1], 1).is_none());
        assert!(Frame::new(FrameSize::new(15, 8), vec![0; 180], 1).is_none());
    }

    #[test]
    fn black_is_limited_range_black_with_neutral_chroma() {
        let data = black(SMALL);
        assert_eq!(data.len(), SMALL.nv12_len());
        assert!(data[..SMALL.luma_len()].iter().all(|&b| b == 16));
        assert!(data[SMALL.luma_len()..].iter().all(|&b| b == 128));
    }

    #[test]
    fn blurring_a_flat_frame_changes_nothing() {
        let src = flat(SMALL, 90, 100, 150);
        assert_eq!(blur(&src, SMALL, 3), src);
    }

    #[test]
    fn a_zero_radius_copies_the_frame() {
        let mut src = flat(SMALL, 0, 128, 128);
        src[5] = 255;
        assert_eq!(blur(&src, SMALL, 0), src);
    }

    #[test]
    fn blurring_spreads_a_bright_pixel_and_keeps_the_size() {
        let mut src = flat(SMALL, 0, 128, 128);
        src[4 * 16 + 8] = 240;
        let out = blur(&src, SMALL, 2);
        assert_eq!(out.len(), src.len());
        assert!(out[4 * 16 + 8] < 240, "the peak is lowered");
        assert!(out[4 * 16 + 9] > 0, "its neighbours pick some up");
        assert!(out[3 * 16 + 8] > 0, "also vertically");
        assert_eq!(out[0], 0, "far pixels stay dark");
    }

    #[test]
    fn blurring_keeps_chroma_channels_separate() {
        let src = flat(SMALL, 50, 60, 200);
        let out = blur(&src, SMALL, 4);
        for pair in out[SMALL.luma_len()..].chunks(2) {
            assert_eq!(pair, [60, 200]);
        }
    }

    #[test]
    fn a_radius_larger_than_the_frame_does_not_panic() {
        let src = flat(SMALL, 10, 128, 128);
        assert_eq!(blur(&src, SMALL, 500).len(), src.len());
    }

    #[test]
    fn the_blur_of_a_full_hd_frame_is_fast_enough_for_30_fps() {
        let src = flat(FrameSize::HD, 120, 128, 128);
        let started = std::time::Instant::now();
        let _ = blur(&src, FrameSize::HD, 12);
        // Not a benchmark: it only fails if the blur is orders of magnitude too slow in a
        // debug build (a 33 ms budget is for the release build).
        assert!(started.elapsed() < std::time::Duration::from_secs(3));
    }
}
