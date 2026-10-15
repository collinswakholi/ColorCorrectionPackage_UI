//! Flat-field gain: ndarray only. `apply_ffc_float` is sRGB → Lab D65, L*×gain.

// Dense least-squares kernels intentionally index several parallel flat matrices.
#![allow(clippy::needless_range_loop)]

use crate::color::{
    clamp01, lab_to_xyz, linear_rgb_to_xyz, linear_to_srgb, srgb_d65_space, srgb_lstar_d65,
    srgb_to_linear, xyz_to_lab, xyz_to_linear_rgb,
};
use crate::maps::{fit_least_squares, solve_linear_system};
use crate::mlp::{fit_mlp, MlpParams, MlpWeights};
use crate::pls::{auto_ncomp, fit_pls, PLS_TOL};
use crate::stages::GammaModel;
use crate::{Error, Result, FFC_EXTREMITY_CUT_OFF, FFC_EXTREMITY_MAX};
use ndarray::{Array2, Array3};
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use std::borrow::Cow;

const EPSILON: f32 = 1e-12;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum FfcFitMethod {
    Linear,
    #[default]
    Poly,
    Pls,
    Nn,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct FfcFitParams {
    pub method: FfcFitMethod,
    pub bins: usize,
    pub smooth_window: i32,
    pub degree: usize,
    pub upsample: Upsample,
    /// CCP_rs FFC NN default is 10000. Tests may lower this.
    pub nn_max_iter: usize,
    /// CCP_rs `ffc_kwargs.tol` — NN early-stop and PLS NIPALS. Default 1e-8.
    pub tol: f64,
    /// CCP_rs `transform_extremity` cut-off. Gains above this tanh toward `max_val`.
    pub cut_off: f32,
    /// CCP_rs `transform_extremity` ceiling. Must be greater than `cut_off`.
    pub max_val: f32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Upsample {
    Bilinear,
    Cubic,
}

impl Default for FfcFitParams {
    fn default() -> Self {
        Self {
            method: FfcFitMethod::Poly,
            bins: 50,
            smooth_window: 5,
            degree: 5,
            upsample: Upsample::Cubic,
            nn_max_iter: 10_000,
            tol: 1e-8,
            cut_off: FFC_EXTREMITY_CUT_OFF,
            max_val: FFC_EXTREMITY_MAX,
        }
    }
}

/// Keep a usable (cut-off, ceiling) pair. Invalid numbers fall back to CCP_rs 1.3 → 1.8.
pub fn sanitize_ffc_extremity(cut_off: f32, max_val: f32) -> (f32, f32) {
    let cut = if cut_off.is_finite() && cut_off > 0.0 {
        cut_off.clamp(0.5, 4.0)
    } else {
        FFC_EXTREMITY_CUT_OFF
    };
    let max = if max_val.is_finite() && max_val > cut {
        max_val.clamp(cut + 0.01, 16.0)
    } else {
        (cut + 0.5).max(FFC_EXTREMITY_MAX)
    };
    if max > cut {
        (cut, max)
    } else {
        (cut, cut + 0.5)
    }
}

/// Inclusive window mean. `h_win` is half-width (`smooth_window=5` → 2).
/// Empty crop or an inverted range keeps the caller’s 1.0 default.
#[cfg(test)]
fn inclusive_window_mean(src: &Array2<f32>, yc: i32, xc: i32, h_win: i32) -> Option<(f32, usize)> {
    let (ch, cw) = src.dim();
    if ch == 0 || cw == 0 {
        return None;
    }
    let y_lo = (yc - h_win).max(0) as usize;
    let y_hi = (yc + h_win).min(ch as i32 - 1).max(0) as usize;
    let x_lo = (xc - h_win).max(0) as usize;
    let x_hi = (xc + h_win).min(cw as i32 - 1).max(0) as usize;
    if y_hi < y_lo || x_hi < x_lo {
        return None;
    }
    let mut sum = 0.0f32;
    let mut count = 0usize;
    for yy in y_lo..=y_hi {
        for xx in x_lo..=x_hi {
            sum += src[[yy, xx]];
            count += 1;
        }
    }
    (count > 0).then_some((sum / count as f32, count))
}

/// Soft-cap values above `cut_off` toward `max_val` with tanh (CCP 1.3 → 1.8).
pub fn transform_extremity(data: &mut [f32], cut_off: f32, max_val: f32) {
    for v in data.iter_mut() {
        if *v > cut_off {
            *v = cut_off + (max_val - cut_off) * (max_val * (*v - cut_off)).tanh();
        }
    }
}

fn bilinear_sample(src: &Array2<f32>, y: f32, x: f32) -> f32 {
    let (mh, mw) = (src.nrows(), src.ncols());
    if mh == 0 || mw == 0 {
        return 1.0;
    }
    let y0 = (y.floor() as usize).min(mh.saturating_sub(1));
    let x0 = (x.floor() as usize).min(mw.saturating_sub(1));
    let y1 = (y0 + 1).min(mh - 1);
    let x1 = (x0 + 1).min(mw - 1);
    let fy = y - y0 as f32;
    let fx = x - x0 as f32;
    src[[y0, x0]] * (1.0 - fy) * (1.0 - fx)
        + src[[y1, x0]] * fy * (1.0 - fx)
        + src[[y0, x1]] * (1.0 - fy) * fx
        + src[[y1, x1]] * fy * fx
}

fn cubic_weight(t: f32) -> [f32; 4] {
    let t2 = t * t;
    let t3 = t2 * t;
    [
        -0.5 * t3 + t2 - 0.5 * t,
        1.5 * t3 - 2.5 * t2 + 1.0,
        -1.5 * t3 + 2.0 * t2 + 0.5 * t,
        0.5 * t3 - 0.5 * t2,
    ]
}

fn cubic_sample(src: &Array2<f32>, y: f32, x: f32) -> f32 {
    let (mh, mw) = (src.nrows() as i32, src.ncols() as i32);
    if mh == 0 || mw == 0 {
        return 1.0;
    }
    let y0 = y.floor() as i32;
    let x0 = x.floor() as i32;
    let wy = cubic_weight(y - y0 as f32);
    let wx = cubic_weight(x - x0 as f32);
    let mut acc = 0.0;
    for (iy, &ky) in wy.iter().enumerate() {
        let yy = (y0 - 1 + iy as i32).clamp(0, mh - 1) as usize;
        for (ix, &kx) in wx.iter().enumerate() {
            let xx = (x0 - 1 + ix as i32).clamp(0, mw - 1) as usize;
            acc += src[[yy, xx]] * ky * kx;
        }
    }
    acc
}

pub fn resize_gain(src: &Array2<f32>, height: usize, width: usize, mode: Upsample) -> Array2<f32> {
    if src.nrows() == height && src.ncols() == width {
        return src.clone();
    }
    let mut out = Array2::from_elem((height, width), 1.0);
    if height == 0 || width == 0 {
        return out;
    }
    let (mh, mw) = (src.nrows(), src.ncols());
    out.as_slice_mut()
        .expect("new gain array is contiguous")
        .par_chunks_mut(width)
        .enumerate()
        .for_each(|(i, row)| {
            let src_y =
                i as f32 * (mh.saturating_sub(1) as f32) / (height.saturating_sub(1).max(1) as f32);
            for (j, dst) in row.iter_mut().enumerate() {
                let src_x = j as f32 * (mw.saturating_sub(1) as f32)
                    / (width.saturating_sub(1).max(1) as f32);
                *dst = match mode {
                    Upsample::Bilinear => bilinear_sample(src, src_y, src_x),
                    Upsample::Cubic => cubic_sample(src, src_y, src_x),
                };
            }
        });
    out
}

/// sRGB [0,1] HxWx3 → Lab D65, L* *= gain, clip L 0–100, back to sRGB [0,1].
pub fn apply_ffc_float(img_rgb: &Array3<f32>, multiplier: &Array2<f32>) -> Array3<f32> {
    let mut out = img_rgb.clone();
    apply_ffc_inplace(&mut out, multiplier);
    out
}

pub fn apply_ffc_inplace(img_rgb: &mut Array3<f32>, multiplier: &Array2<f32>) {
    apply_ffc_transform_inplace(img_rgb, multiplier, None);
}

pub fn sample_gain_at(
    multiplier: &Array2<f32>,
    x: f64,
    y: f64,
    image_width: u32,
    image_height: u32,
) -> f32 {
    let gx = x.clamp(0.0, image_width.saturating_sub(1) as f64)
        * multiplier.ncols().saturating_sub(1) as f64
        / image_width.saturating_sub(1).max(1) as f64;
    let gy = y.clamp(0.0, image_height.saturating_sub(1) as f64)
        * multiplier.nrows().saturating_sub(1) as f64
        / image_height.saturating_sub(1).max(1) as f64;
    bilinear_sample(multiplier, gy as f32, gx as f32)
}

pub fn apply_ffc_pixel(rgb: crate::color::Vec3, gain: f32) -> crate::color::Vec3 {
    let (d65, srgb_space) = srgb_d65_space();
    let lab = xyz_to_lab(linear_rgb_to_xyz(srgb_to_linear(rgb), srgb_space), d65);
    let xyz = lab_to_xyz(
        crate::color::Lab {
            l: (lab.l * gain as f64).clamp(0.0, 100.0),
            a: lab.a,
            b: lab.b,
        },
        d65,
    );
    crate::color::clamp01_vec(linear_to_srgb(xyz_to_linear_rgb(xyz, srgb_space)))
}

fn apply_ffc_transform_inplace(
    img_rgb: &mut Array3<f32>,
    multiplier: &Array2<f32>,
    gc: Option<&GammaModel>,
) {
    let (h, w, _) = (img_rgb.shape()[0], img_rgb.shape()[1], img_rgb.shape()[2]);
    let gain: Cow<'_, Array2<f32>> = if multiplier.shape() == [h, w] {
        Cow::Borrowed(multiplier)
    } else {
        Cow::Owned(resize_gain(multiplier, h, w, Upsample::Bilinear))
    };
    let (d65, srgb_space) = srgb_d65_space();
    let gain_src = if let Some(slice) = gain.as_slice() {
        Cow::Borrowed(slice)
    } else {
        Cow::Owned(gain.iter().copied().collect())
    };
    if img_rgb.as_slice_mut().is_none() {
        let dim = img_rgb.raw_dim();
        let packed: Vec<f32> = img_rgb.iter().copied().collect();
        *img_rgb = Array3::from_shape_vec(dim, packed).expect("valid RGB shape");
    }
    img_rgb
        .as_slice_mut()
        .expect("packed RGB image")
        .par_chunks_exact_mut(3)
        .zip(gain_src.par_iter())
        .for_each(|(px, &g)| {
            let rgb = [px[0] as f64, px[1] as f64, px[2] as f64];
            let lab = xyz_to_lab(linear_rgb_to_xyz(srgb_to_linear(rgb), srgb_space), d65);
            let l_ffc = (lab.l * g as f64).clamp(0.0, 100.0);
            let l = gc
                .map(|model| model.map_lstar(l_ffc))
                .filter(|value| value.is_finite())
                .unwrap_or(l_ffc)
                .clamp(0.0, 100.0);
            let xyz = lab_to_xyz(
                crate::color::Lab {
                    l,
                    a: lab.a,
                    b: lab.b,
                },
                d65,
            );
            let srgb =
                crate::color::clamp01_vec(linear_to_srgb(xyz_to_linear_rgb(xyz, srgb_space)));
            px[0] = srgb[0] as f32;
            px[1] = srgb[1] as f32;
            px[2] = srgb[2] as f32;
        });
}

/// One sRGB↔Lab trip: L* × gain, then the GC curve on L*. Matches FFC-then-GC order
/// without the intermediate encode.
pub fn apply_ffc_gc_fused(
    img_rgb: &Array3<f32>,
    multiplier: &Array2<f32>,
    gc: &GammaModel,
) -> Array3<f32> {
    let mut out = img_rgb.clone();
    apply_ffc_gc_fused_inplace(&mut out, multiplier, gc);
    out
}

pub fn apply_ffc_gc_fused_inplace(
    img_rgb: &mut Array3<f32>,
    multiplier: &Array2<f32>,
    gc: &GammaModel,
) {
    apply_ffc_transform_inplace(img_rgb, multiplier, Some(gc));
}

fn rgb_slice(img: &Array3<f32>) -> Cow<'_, [f32]> {
    if let Some(slice) = img.as_slice() {
        Cow::Borrowed(slice)
    } else {
        Cow::Owned(img.iter().copied().collect())
    }
}

fn max_lstar_in_crop(
    src: &[f32],
    full_width: usize,
    x1: usize,
    y1: usize,
    x2: usize,
    y2: usize,
) -> f32 {
    (y1..y2)
        .into_par_iter()
        .map(|y| {
            let start = (y * full_width + x1) * 3;
            let end = (y * full_width + x2) * 3;
            src[start..end]
                .chunks_exact(3)
                .map(|px| srgb_lstar_d65([px[0] as f64, px[1] as f64, px[2] as f64]) as f32)
                .fold(EPSILON, |max_l, l| max_l.max(l.max(EPSILON)))
        })
        .reduce(|| EPSILON, f32::max)
}

#[allow(clippy::too_many_arguments)]
fn gain_window_mean(
    src: &[f32],
    full_width: usize,
    crop_width: usize,
    crop_height: usize,
    x1: usize,
    y1: usize,
    yc: i32,
    xc: i32,
    h_win: i32,
    max_l: f32,
) -> Option<(f32, usize)> {
    if crop_width == 0 || crop_height == 0 {
        return None;
    }
    let y_lo = (yc - h_win).max(0) as usize;
    let y_hi = (yc + h_win).min(crop_height as i32 - 1).max(0) as usize;
    let x_lo = (xc - h_win).max(0) as usize;
    let x_hi = (xc + h_win).min(crop_width as i32 - 1).max(0) as usize;
    if y_hi < y_lo || x_hi < x_lo {
        return None;
    }
    let mut sum = 0.0f32;
    let mut count = 0usize;
    for crop_y in y_lo..=y_hi {
        let start = ((y1 + crop_y) * full_width + x1 + x_lo) * 3;
        let end = ((y1 + crop_y) * full_width + x1 + x_hi + 1) * 3;
        for px in src[start..end].chunks_exact(3) {
            let l = srgb_lstar_d65([px[0] as f64, px[1] as f64, px[2] as f64]) as f32;
            sum += max_l / l.max(EPSILON);
            count += 1;
        }
    }
    (count > 0).then_some((sum / count as f32, count))
}

fn poly_xy_features(
    x: f64,
    y: f64,
    degree: usize,
    include_bias: bool,
    interaction_only: bool,
) -> Vec<f64> {
    let mut out = Vec::new();
    let start = if include_bias { 0 } else { 1 };
    for total in start..=degree {
        for i in (0..=total).rev() {
            let j = total - i;
            if interaction_only && (i > 1 || j > 1) {
                continue;
            }
            out.push(x.powi(i as i32) * y.powi(j as i32));
        }
    }
    out
}

fn design_xy(xs: &[f64], ys: &[f64], degree: usize, interaction_only: bool) -> (Vec<f64>, usize) {
    let n = xs.len();
    let p = poly_xy_features(0.25, 0.25, degree, true, interaction_only).len();
    let mut x = vec![0.0; n * p];
    for i in 0..n {
        let feats = poly_xy_features(xs[i], ys[i], degree, true, interaction_only);
        x[i * p..i * p + p].copy_from_slice(&feats);
    }
    (x, p)
}

fn fit_surface(xs: &[f64], ys: &[f64], zs: &[f64], degree: usize, ridge: f64) -> Result<Vec<f64>> {
    let n = xs.len();
    let (x, p) = design_xy(xs, ys, degree, false);
    let mut y = vec![0.0; n * 3];
    for i in 0..n {
        y[i * 3] = zs[i];
    }
    let coeffs = fit_least_squares(&x, &y, n, p, ridge)?;
    Ok((0..p).map(|j| coeffs[j * 3]).collect())
}

fn fit_linear_surface(xs: &[f64], ys: &[f64], zs: &[f64], degree: usize) -> Result<Vec<f64>> {
    let n = xs.len();
    let (x, p) = design_xy(xs, ys, degree, false);
    let mut xtx = vec![0.0; p * p];
    let mut xtz = vec![0.0; p];
    for i in 0..n {
        let base = i * p;
        for a in 0..p {
            let xa = x[base + a];
            xtz[a] += xa * zs[i];
            for b in 0..p {
                xtx[a * p + b] += xa * x[base + b];
            }
        }
    }
    solve_linear_system(xtx, xtz, p)
}

fn predict_surface(x: f64, y: f64, degree: usize, coeffs: &[f64], interaction_only: bool) -> f64 {
    let feats = poly_xy_features(x, y, degree, true, interaction_only);
    feats.iter().zip(coeffs.iter()).map(|(f, c)| f * c).sum()
}

enum FfcSurface {
    Poly {
        degree: usize,
        interaction_only: bool,
        coeffs: Vec<f64>,
        intercept: f64,
    },
    Nn {
        degree: usize,
        interaction_only: bool,
        net: MlpWeights,
    },
}

impl FfcSurface {
    fn predict(&self, x: f64, y: f64) -> f64 {
        match self {
            Self::Poly {
                degree,
                interaction_only,
                coeffs,
                intercept,
            } => predict_surface(x, y, *degree, coeffs, *interaction_only) + intercept,
            Self::Nn {
                degree,
                interaction_only,
                net,
            } => {
                let feats = poly_xy_features(x, y, *degree, true, *interaction_only);
                net.predict1(&feats)
            }
        }
    }
}

/// Parse `"x1,y1,x2,y2"` → `(x, y, w, h)` with `w = x2 - x1`.
pub fn parse_crop_rect(text: &str) -> Result<[i32; 4]> {
    let parts: Vec<&str> = text.split(',').map(str::trim).collect();
    if parts.len() != 4 {
        return Err(Error::InvalidArgument(
            "crop_rect must be x1,y1,x2,y2".into(),
        ));
    }
    let nums: Result<Vec<i32>> = parts
        .iter()
        .map(|p| {
            p.parse::<i32>()
                .map_err(|_| Error::InvalidArgument(format!("bad crop_rect value {p}")))
        })
        .collect();
    let n = nums?;
    let w = n[2] - n[0];
    let h = n[3] - n[1];
    if w <= 0 || h <= 0 {
        return Err(Error::InvalidArgument(
            "crop_rect has non-positive size".into(),
        ));
    }
    Ok([n[0], n[1], w, h])
}

/// Fit a full-frame gain map. Missing/empty white → identity ones.
pub fn fit_gain_map(
    white: Option<&Array3<f32>>,
    crop_rect: Option<[i32; 4]>,
    params: &FfcFitParams,
) -> Result<Array2<f32>> {
    let Some(img) = white else {
        return Ok(Array2::from_elem((1, 1), 1.0));
    };
    let (full_h, full_w, _) = (img.shape()[0], img.shape()[1], img.shape()[2]);
    if full_h == 0 || full_w == 0 {
        return Ok(Array2::from_elem((1, 1), 1.0));
    }

    let (x1, y1, x2, y2) = if let Some([x, y, w, h]) = crop_rect {
        let x1 = x.clamp(0, full_w as i32 - 1);
        let y1 = y.clamp(0, full_h as i32 - 1);
        let x2 = (x + w).clamp(x1 + 1, full_w as i32);
        let y2 = (y + h).clamp(y1 + 1, full_h as i32);
        (x1, y1, x2, y2)
    } else {
        (0, 0, full_w as i32, full_h as i32)
    };

    let ch = (y2 - y1) as usize;
    let cw = (x2 - x1) as usize;
    let src = rgb_slice(img);
    let max_l = max_lstar_in_crop(
        src.as_ref(),
        full_w,
        x1 as usize,
        y1 as usize,
        x2 as usize,
        y2 as usize,
    );

    let bins = params.bins.max(2);
    let h_win = (params.smooth_window - 1).max(0) / 2;
    let mut z_m = vec![1.0_f32; bins * bins];
    z_m.par_chunks_mut(bins).enumerate().for_each(|(i, row)| {
        let yc = (i as f32 * (ch.saturating_sub(1) as f32) / (bins - 1) as f32) as i32;
        for (j, value) in row.iter_mut().enumerate() {
            let xc = (j as f32 * (cw.saturating_sub(1) as f32) / (bins - 1) as f32) as i32;
            if let Some((mean, _)) = gain_window_mean(
                src.as_ref(),
                full_w,
                cw,
                ch,
                x1 as usize,
                y1 as usize,
                yc,
                xc,
                h_win,
                max_l,
            ) {
                *value = mean;
            }
        }
    });

    let z_min = z_m.iter().copied().fold(f32::INFINITY, f32::min);
    let z_max = z_m.iter().copied().fold(f32::NEG_INFINITY, f32::max);
    let z_range = z_max - z_min + EPSILON;

    let mut xs = Vec::with_capacity(bins * bins);
    let mut ys = Vec::with_capacity(bins * bins);
    let mut zs = Vec::with_capacity(bins * bins);
    for i in 0..bins {
        let gy = y1 as f32 + i as f32 * (y2 - y1) as f32 / (bins - 1) as f32;
        for j in 0..bins {
            let gx = x1 as f32 + j as f32 * (x2 - x1) as f32 / (bins - 1) as f32;
            xs.push(gx as f64 / (full_w as f64 + EPSILON as f64));
            ys.push(gy as f64 / (full_h as f64 + EPSILON as f64));
            zs.push(((z_m[i * bins + j] - z_min) / z_range) as f64);
        }
    }

    let degree = match params.method {
        FfcFitMethod::Linear => 1,
        _ => params.degree.max(1),
    };
    let surface = match params.method {
        FfcFitMethod::Linear => {
            let coeffs = fit_linear_surface(&xs, &ys, &zs, degree)?;
            FfcSurface::Poly {
                degree,
                interaction_only: false,
                coeffs,
                intercept: 0.0,
            }
        }
        FfcFitMethod::Poly => {
            let coeffs = fit_surface(&xs, &ys, &zs, degree, 1e-8)?;
            FfcSurface::Poly {
                degree,
                interaction_only: false,
                coeffs,
                intercept: 0.0,
            }
        }
        FfcFitMethod::Pls => {
            let n = xs.len();
            let (x, p) = design_xy(&xs, &ys, degree, true);
            let ncomp = auto_ncomp(n, p, 0);
            let pls_tol = if params.tol.is_finite() && params.tol > 0.0 {
                params.tol
            } else {
                PLS_TOL
            };
            let (coeffs, intercept) = fit_pls(&x, &zs, n, p, 1, ncomp, pls_tol)?;
            FfcSurface::Poly {
                degree,
                interaction_only: true,
                coeffs,
                intercept: intercept.first().copied().unwrap_or(0.0),
            }
        }
        FfcFitMethod::Nn => {
            let n = xs.len();
            let (x, p) = design_xy(&xs, &ys, degree, true);
            let mut mlp = MlpParams::for_ffc_iters(params.nn_max_iter.max(1));
            if params.tol.is_finite() && params.tol > 0.0 {
                mlp.tol = params.tol;
            }
            let net = fit_mlp(&x, &zs, n, p, &mlp);
            FfcSurface::Nn {
                degree,
                interaction_only: true,
                net,
            }
        }
    };

    let mut coarse = Array2::<f32>::zeros((bins, bins));
    for i in 0..bins {
        let gy = i as f32 * (full_h.saturating_sub(1) as f32) / (bins - 1) as f32;
        for j in 0..bins {
            let gx = j as f32 * (full_w.saturating_sub(1) as f32) / (bins - 1) as f32;
            let xn = gx as f64 / (full_w as f64 + EPSILON as f64);
            let yn = gy as f64 / (full_h as f64 + EPSILON as f64);
            let pred = surface.predict(xn, yn) as f32 * z_range + z_min;
            coarse[[i, j]] = pred;
        }
    }
    let mut flat: Vec<f32> = coarse.iter().copied().collect();
    let (cut, max) = sanitize_ffc_extremity(params.cut_off, params.max_val);
    transform_extremity(&mut flat, cut, max);
    for (i, v) in flat.into_iter().enumerate() {
        coarse[[i / bins, i % bins]] = v;
    }
    Ok(resize_gain(&coarse, full_h, full_w, params.upsample))
}

pub fn compute_multiplier(
    white: Option<&Array3<f32>>,
    crop_rect: Option<[i32; 4]>,
    params: &FfcFitParams,
) -> Result<Array2<f32>> {
    fit_gain_map(white, crop_rect, params)
}

pub fn identity_gain(height: usize, width: usize) -> Array2<f32> {
    Array2::from_elem((height.max(1), width.max(1)), 1.0)
}

/// Keep `solve_linear_system` referenced for 1-D FFC fallback surfaces.
#[allow(dead_code)]
fn ridge_solve_1d(xtx: Vec<f64>, rhs: Vec<f64>, n: usize) -> Result<Vec<f64>> {
    solve_linear_system(xtx, rhs, n)
}

pub fn clip01(v: f32) -> f32 {
    clamp01(v as f64) as f32
}

#[cfg(test)]
mod tests {
    use super::*;
    use ndarray::Array3;

    fn slanted_white() -> Array3<f32> {
        let mut img = Array3::<f32>::zeros((32, 32, 3));
        for y in 0..32 {
            for x in 0..32 {
                let v = 0.4 + 0.4 * (x as f32 / 31.0) + 0.15 * (y as f32 / 31.0);
                img[[y, x, 0]] = v;
                img[[y, x, 1]] = v;
                img[[y, x, 2]] = v;
            }
        }
        img
    }

    #[test]
    fn ffc_pls_and_nn_are_not_poly_aliases() {
        let img = slanted_white();
        let poly = FfcFitParams {
            method: FfcFitMethod::Poly,
            bins: 8,
            degree: 3,
            nn_max_iter: 200,
            ..Default::default()
        };
        let mut pls = poly.clone();
        pls.method = FfcFitMethod::Pls;
        let mut nn = poly.clone();
        nn.method = FfcFitMethod::Nn;
        let gp = fit_gain_map(Some(&img), None, &poly).unwrap();
        let gs = fit_gain_map(Some(&img), None, &pls).unwrap();
        let gn = fit_gain_map(Some(&img), None, &nn).unwrap();
        let d_pls: f32 = gp.iter().zip(gs.iter()).map(|(a, b)| (a - b).abs()).sum();
        let d_nn: f32 = gp.iter().zip(gn.iter()).map(|(a, b)| (a - b).abs()).sum();
        assert!(
            d_pls > 1e-4 || d_nn > 1e-4,
            "PLS/NN must not be poly aliases (d_pls={d_pls}, d_nn={d_nn})"
        );
        assert!(gs.iter().all(|v| v.is_finite()));
        assert!(gn.iter().all(|v| v.is_finite()));
    }

    #[test]
    fn smooth_window_five_visits_25_center_samples() {
        let src = Array2::from_elem((64, 64), 1.5f32);
        let (_, count) = inclusive_window_mean(&src, 31, 31, 2).expect("center window");
        assert_eq!(count, 25, "smooth_window=5 must average a 5×5 neighborhood");
        let (mean, n1) = inclusive_window_mean(&src, 31, 31, 0).expect("window=1");
        assert_eq!(n1, 1, "smooth_window=1 must visit the center bin");
        assert!((mean - 1.5).abs() < 1e-6);
    }

    #[test]
    fn transform_extremity_soft_caps_above_cutoff() {
        let mut data = vec![1.0f32, 1.3, 2.0, 4.0];
        transform_extremity(&mut data, 1.3, 1.8);
        assert!((data[0] - 1.0).abs() < 1e-6);
        assert!((data[1] - 1.3).abs() < 1e-6);
        assert!(data[2] > 1.3 && data[2] < 1.8);
        assert!(data[3] > data[2] && data[3] < 1.8);
        let (cut, max) = sanitize_ffc_extremity(f32::NAN, 0.0);
        assert!((cut - 1.3).abs() < 1e-6);
        assert!(max > cut);
    }

    #[test]
    fn fused_ffc_gc_stays_in_range() {
        let mut img = Array3::<f32>::zeros((6, 6, 3));
        for y in 0..6 {
            for x in 0..6 {
                img[[y, x, 0]] = 0.42;
                img[[y, x, 1]] = 0.35;
                img[[y, x, 2]] = 0.28;
            }
        }
        let gain = Array2::from_elem((6, 6), 1.2);
        let gc = GammaModel::legacy(vec![1.05, 0.0]);
        let fused = apply_ffc_gc_fused(&img, &gain, &gc);
        assert!(fused
            .iter()
            .all(|v| v.is_finite() && *v >= 0.0 && *v <= 1.0));
        let diff: f32 = img
            .iter()
            .zip(fused.iter())
            .map(|(a, b)| (a - b).abs())
            .sum();
        assert!(diff > 1e-3, "fused FFC+GC should change pixels ({diff})");
    }
}
