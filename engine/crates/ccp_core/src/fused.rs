//! Fast-mode flat-field correction fused with the combined 3-D LUT.
//!
//! The compatibility path (`ffc::apply_ffc_inplace` then a LUT) makes two
//! full-image passes and evaluates FFC in f64. Fast mode already accepts the
//! 65³ LUT approximation, so it evaluates FFC in f32 and samples the LUT in the
//! same pass. The result matches the compatibility path to within 1/255 at
//! 8 bits; the GPU kernel (`ccp_detect`'s `ffc_lut_wgpu.wgsl`) uses the same
//! formulas.
//!
//! FFC scales only L* and keeps a* and b*. Because `L = 116·fy − 16`,
//! `a = 500·(fx − fy)` and `b = 200·(fy − fz)`, that is the same as shifting
//! fx, fy and fz by one amount, which avoids building Lab explicitly.

use ndarray::Array2;
use rayon::prelude::*;

use crate::color::srgb_d65_space;

const LAB_EPSILON: f32 = 216.0 / 24389.0;
const LAB_KAPPA: f32 = 24389.0 / 27.0;
const LAB_DELTA: f32 = 6.0 / 29.0;

/// The sRGB/D65 constants the fused kernels need, in f32.
#[derive(Clone, Copy, Debug)]
pub struct FfcConstants {
    /// Row-major linear sRGB → XYZ.
    pub rgb_to_xyz: [f32; 9],
    /// Row-major XYZ → linear sRGB.
    pub xyz_to_rgb: [f32; 9],
    /// D65 white XYZ.
    pub white: [f32; 3],
}

impl FfcConstants {
    pub fn srgb_d65() -> Self {
        let (white, space) = srgb_d65_space();
        Self {
            rgb_to_xyz: space.rgb_to_xyz.map(|v| v as f32),
            xyz_to_rgb: space.xyz_to_rgb.map(|v| v as f32),
            white: white.xyz.map(|v| v as f32),
        }
    }
}

/// Where each pixel's gain comes from: the gain map at the image's size, or a
/// map of another size sampled bilinearly exactly as `ffc::resize_gain` does.
#[derive(Clone, Copy, Debug)]
pub struct GainGrid<'a> {
    pub values: &'a [f32],
    pub width: usize,
    pub height: usize,
}

impl<'a> GainGrid<'a> {
    /// `None` when the map is not contiguous or is empty.
    pub fn new(gain: &'a Array2<f32>) -> Option<Self> {
        let values = gain.as_slice()?;
        (!values.is_empty()).then_some(Self {
            values,
            width: gain.ncols(),
            height: gain.nrows(),
        })
    }

    fn at(&self, y: usize, x: usize) -> f32 {
        self.values[y * self.width + x]
    }

    /// Source row (`y0`, `y1`, weight) for image row `row` of `height` rows.
    fn row(&self, row: usize, height: usize) -> (usize, usize, f32) {
        let y = row as f32 * (self.height.saturating_sub(1) as f32)
            / (height.saturating_sub(1).max(1) as f32);
        let y0 = (y.floor() as usize).min(self.height - 1);
        (y0, (y0 + 1).min(self.height - 1), y - y0 as f32)
    }

    fn sample(&self, (y0, y1, fy): (usize, usize, f32), column: usize, width: usize) -> f32 {
        let x = column as f32 * (self.width.saturating_sub(1) as f32)
            / (width.saturating_sub(1).max(1) as f32);
        let x0 = (x.floor() as usize).min(self.width - 1);
        let x1 = (x0 + 1).min(self.width - 1);
        let fx = x - x0 as f32;
        self.at(y0, x0) * (1.0 - fy) * (1.0 - fx)
            + self.at(y1, x0) * fy * (1.0 - fx)
            + self.at(y0, x1) * (1.0 - fy) * fx
            + self.at(y1, x1) * fy * fx
    }
}

fn lab_f(t: f32) -> f32 {
    if t > LAB_EPSILON {
        t.cbrt()
    } else {
        (LAB_KAPPA * t + 16.0) / 116.0
    }
}

fn lab_f_inv(f: f32) -> f32 {
    if f > LAB_DELTA {
        f * f * f
    } else {
        (116.0 * f - 16.0) / LAB_KAPPA
    }
}

fn to_linear(c: f32) -> f32 {
    let c = c.clamp(0.0, 1.0);
    if c <= 0.04045 {
        c / 12.92
    } else {
        ((c + 0.055) / 1.055).powf(2.4)
    }
}

fn encode(v: f32) -> f32 {
    let v = v.clamp(0.0, 1.0);
    if v <= 0.003_130_8 {
        v * 12.92
    } else {
        1.055 * v.powf(1.0 / 2.4) - 0.055
    }
}

fn mul(m: &[f32; 9], v: [f32; 3]) -> [f32; 3] {
    [
        m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
        m[3] * v[0] + m[4] * v[1] + m[5] * v[2],
        m[6] * v[0] + m[7] * v[1] + m[8] * v[2],
    ]
}

/// FFC of one encoded sRGB pixel in f32: L* × `gain`, clipped to 0–100.
pub fn ffc_pixel_f32(rgb: [f32; 3], gain: f32, c: &FfcConstants) -> [f32; 3] {
    let lin = rgb.map(to_linear);
    let xyz = mul(&c.rgb_to_xyz, lin);
    let f = [
        lab_f(xyz[0] / c.white[0]),
        lab_f(xyz[1] / c.white[1]),
        lab_f(xyz[2] / c.white[2]),
    ];
    let l = ((116.0 * f[1] - 16.0) * gain).clamp(0.0, 100.0);
    let shift = (l + 16.0) / 116.0 - f[1];
    let xyz = [
        c.white[0] * lab_f_inv(f[0] + shift),
        c.white[1] * lab_f_inv(f[1] + shift),
        c.white[2] * lab_f_inv(f[2] + shift),
    ];
    mul(&c.xyz_to_rgb, xyz).map(encode)
}

/// Trilinear sample of an encoded `gs`³ RGB LUT (red fastest), in f32.
pub fn sample_lut_f32(lut: &[f32], gs: usize, rgb: [f32; 3]) -> [f32; 3] {
    let max = (gs - 1) as f32;
    let p = rgb.map(|v| v.clamp(0.0, 1.0) * max);
    let lo = p.map(|v| (v.floor() as usize).min(gs - 1));
    let hi = lo.map(|v| (v + 1).min(gs - 1));
    let t = [
        p[0] - lo[0] as f32,
        p[1] - lo[1] as f32,
        p[2] - lo[2] as f32,
    ];
    let at = |r: usize, g: usize, b: usize| {
        let i = ((b * gs + g) * gs + r) * 3;
        [lut[i], lut[i + 1], lut[i + 2]]
    };
    let mix = |a: [f32; 3], b: [f32; 3], t: f32| {
        [
            a[0] + (b[0] - a[0]) * t,
            a[1] + (b[1] - a[1]) * t,
            a[2] + (b[2] - a[2]) * t,
        ]
    };
    let c00 = mix(at(lo[0], lo[1], lo[2]), at(hi[0], lo[1], lo[2]), t[0]);
    let c10 = mix(at(lo[0], hi[1], lo[2]), at(hi[0], hi[1], lo[2]), t[0]);
    let c01 = mix(at(lo[0], lo[1], hi[2]), at(hi[0], lo[1], hi[2]), t[0]);
    let c11 = mix(at(lo[0], hi[1], hi[2]), at(hi[0], hi[1], hi[2]), t[0]);
    mix(mix(c00, c10, t[1]), mix(c01, c11, t[1]), t[2])
}

/// FFC then the encoded LUT, in place, on rows `first_row..` of a packed
/// `width`-pixel-wide RGB image of `height` rows. `rgb` holds whole rows.
pub fn apply_ffc_lut_rows(
    rgb: &mut [f32],
    width: usize,
    height: usize,
    first_row: usize,
    gain: GainGrid<'_>,
    lut: Option<(&[f32], usize)>,
) {
    let c = FfcConstants::srgb_d65();
    let same_size = gain.width == width && gain.height == height;
    rgb.par_chunks_exact_mut(width * 3)
        .enumerate()
        .for_each(|(i, row)| {
            let y = first_row + i;
            let source_row = gain.row(y, height);
            for (x, px) in row.chunks_exact_mut(3).enumerate() {
                let g = if same_size {
                    gain.at(y, x)
                } else {
                    gain.sample(source_row, x, width)
                };
                let corrected = ffc_pixel_f32([px[0], px[1], px[2]], g, &c);
                let out = match lut {
                    Some((table, gs)) => sample_lut_f32(table, gs, corrected),
                    None => corrected,
                };
                px.copy_from_slice(&out);
            }
        });
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffc::{apply_ffc_inplace, apply_ffc_pixel};
    use crate::maps::sample_encoded_lut;
    use ndarray::Array3;

    fn lut(gs: usize) -> Vec<f32> {
        let mut out = Vec::with_capacity(gs * gs * gs * 3);
        for b in 0..gs {
            for g in 0..gs {
                for r in 0..gs {
                    let v = [r, g, b].map(|c| c as f32 / (gs - 1) as f32);
                    out.extend([
                        (1.05 * v[0] - 0.04 * v[1]).powf(1.1),
                        0.97 * v[1] + 0.02 * v[2],
                        (v[2] * 0.9 + 0.05 * v[0]).sqrt(),
                    ]);
                }
            }
        }
        out
    }

    #[test]
    fn f32_ffc_matches_the_f64_reference_across_the_cube() {
        let c = FfcConstants::srgb_d65();
        let mut worst = 0f64;
        for i in 0..17 * 17 * 17 * 5 {
            let rgb = [i % 17, (i / 17) % 17, (i / 289) % 17].map(|v| v as f32 / 16.0);
            let gain = [0.6, 0.95, 1.0, 1.2, 1.8][i / 4913];
            let fast = ffc_pixel_f32(rgb, gain, &c);
            let exact = apply_ffc_pixel(rgb.map(f64::from), gain);
            for ch in 0..3 {
                worst = worst.max((fast[ch] as f64 - exact[ch]).abs());
            }
        }
        assert!(worst < 2e-4, "max f32/f64 FFC difference {worst}");
    }

    #[test]
    fn fused_rows_match_ffc_then_lut_for_resized_and_same_size_gain() {
        let (h, w) = (7, 9);
        let mut image = Array3::<f32>::zeros((h, w, 3));
        for ((y, x, ch), v) in image.indexed_iter_mut() {
            *v = ((y * 31 + x * 17 + ch * 7) % 23) as f32 / 22.0;
        }
        let table = lut(5);
        for (gh, gw) in [(h, w), (3, 4)] {
            let gain =
                Array2::from_shape_fn((gh, gw), |(y, x)| 0.8 + 0.05 * y as f32 + 0.03 * x as f32);
            let mut expected = image.clone();
            apply_ffc_inplace(&mut expected, &gain);
            for px in expected.as_slice_mut().unwrap().chunks_exact_mut(3) {
                let o = sample_encoded_lut(&table, 5, [px[0], px[1], px[2]].map(f64::from));
                px.copy_from_slice(&o.map(|v| v as f32));
            }
            let mut fused = image.clone();
            // Two calls on row ranges must equal one call on the whole image.
            let (top, bottom) = fused.as_slice_mut().unwrap().split_at_mut(3 * w * 3);
            apply_ffc_lut_rows(
                top,
                w,
                h,
                0,
                GainGrid::new(&gain).unwrap(),
                Some((&table, 5)),
            );
            apply_ffc_lut_rows(
                bottom,
                w,
                h,
                3,
                GainGrid::new(&gain).unwrap(),
                Some((&table, 5)),
            );
            let worst = expected
                .iter()
                .zip(fused.iter())
                .map(|(a, b)| (a - b).abs())
                .fold(0f32, f32::max);
            assert!(worst < 5e-4, "gain {gh}x{gw}: max difference {worst}");
        }
    }
}
