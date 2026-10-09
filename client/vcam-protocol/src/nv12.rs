//! NV12 helpers both sides need: the client builds frames with them and the camera source adapts
//! a frame to whatever size the consuming application asked for.
//!
//! NV12 is a full-resolution Y plane followed by one interleaved UV plane at half resolution in
//! both directions, so a frame of `width x height` is `width * height * 3 / 2` bytes. Sizes must be
//! even in both directions.

/// Bytes of the Y plane.
pub fn luma_len(width: u32, height: u32) -> usize {
    width as usize * height as usize
}

/// Black in limited-range NV12: luma 16, neutral chroma 128.
pub fn black(width: u32, height: u32) -> Vec<u8> {
    let luma = luma_len(width, height);
    let mut data = vec![128u8; luma * 3 / 2];
    data[..luma].fill(16);
    data
}

/// Scales a frame by nearest neighbour. Fine for a camera that cannot deliver the wanted size, and
/// cheap enough to do for every frame. `src` must be one frame of `from` and both sizes valid NV12
/// sizes; otherwise black of the target size is returned rather than reading out of range.
pub fn scale(src: &[u8], from: (u32, u32), to: (u32, u32)) -> Vec<u8> {
    let (fw, fh) = (from.0 as usize, from.1 as usize);
    let (tw, th) = (to.0 as usize, to.1 as usize);
    let valid = |w: usize, h: usize| w >= 2 && h >= 2 && w.is_multiple_of(2) && h.is_multiple_of(2);
    if !valid(fw, fh) || !valid(tw, th) || src.len() != fw * fh * 3 / 2 {
        return black(to.0, to.1);
    }
    if from == to {
        return src.to_vec();
    }
    let mut out = vec![0u8; tw * th * 3 / 2];
    for y in 0..th {
        let sy = y * fh / th;
        for x in 0..tw {
            out[y * tw + x] = src[sy * fw + x * fw / tw];
        }
    }
    let (src_chroma, dst_chroma) = (&src[fw * fh..], &mut out[tw * th..]);
    for y in 0..th / 2 {
        let sy = y * (fh / 2) / (th / 2);
        for x in 0..tw / 2 {
            let sx = x * (fw / 2) / (tw / 2);
            let (s, d) = ((sy * (fw / 2) + sx) * 2, (y * (tw / 2) + x) * 2);
            dst_chroma[d] = src_chroma[s];
            dst_chroma[d + 1] = src_chroma[s + 1];
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn flat(w: u32, h: u32, y: u8, u: u8, v: u8) -> Vec<u8> {
        let mut data = vec![y; luma_len(w, h)];
        for _ in 0..luma_len(w, h) / 4 {
            data.push(u);
            data.push(v);
        }
        data
    }

    #[test]
    fn black_is_limited_range_black_with_neutral_chroma() {
        let data = black(16, 8);
        assert_eq!(data.len(), 16 * 8 * 3 / 2);
        assert!(data[..128].iter().all(|&b| b == 16));
        assert!(data[128..].iter().all(|&b| b == 128));
    }

    #[test]
    fn scaling_to_the_same_size_copies() {
        let src = flat(16, 8, 7, 8, 9);
        assert_eq!(scale(&src, (16, 8), (16, 8)), src);
    }

    #[test]
    fn scaling_a_flat_frame_stays_flat_at_any_size() {
        let src = flat(1280, 720, 90, 100, 150);
        let down = scale(&src, (1280, 720), (640, 480));
        assert_eq!(down, flat(640, 480, 90, 100, 150));
        assert_eq!(scale(&down, (640, 480), (1280, 720)), src);
    }

    #[test]
    fn scaling_down_by_two_samples_every_second_pixel() {
        let mut src = vec![0u8; 8 * 4 * 3 / 2];
        for (i, b) in src[..32].iter_mut().enumerate() {
            *b = i as u8;
        }
        let out = scale(&src, (8, 4), (4, 2));
        assert_eq!(&out[..4], [0, 2, 4, 6]);
        assert_eq!(&out[4..8], [16, 18, 20, 22]);
    }

    #[test]
    fn a_wrong_sized_source_yields_black_not_a_panic() {
        let out = scale(&[1, 2, 3], (16, 8), (8, 4));
        assert_eq!(out, black(8, 4));
        assert_eq!(scale(&flat(16, 8, 1, 1, 1), (15, 8), (8, 4)), black(8, 4));
        // An odd target size is not a valid NV12 size: the result is black of that size, not a
        // frame built from out-of-range reads.
        assert_eq!(scale(&flat(16, 8, 1, 1, 1), (16, 8), (7, 4)), black(7, 4));
    }
}
