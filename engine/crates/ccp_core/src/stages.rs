//! Sat, GC, and WB. Neutrals are named only — never last-6 of the measured array.

// Small fixed-channel and polynomial kernels intentionally index parallel buffers.
#![allow(clippy::needless_range_loop)]

use crate::color::{
    lab_d50_to_srgb, lab_d50_to_target_lab, lab_to_xyz, linear_to_srgb, parse_white_point,
    srgb_d65_space, srgb_to_lab_d65, srgb_to_linear, xyz_to_linear_rgb, Lab, RgbColorSpace, Vec3,
    WhitePoint,
};
#[cfg(test)]
use crate::maps::fit_least_squares;
use crate::maps::{CorrectionModel, CorrectionSettings};
use crate::refs::{lookup_neutrals, ChartPatch, ChartSpec};
use crate::{Error, Result, RGB_CLIP_HI, RGB_CLIP_LO, SATURATION_THRESHOLD, WB_DIAG_SCALE};
use ndarray::Array3;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

const MIN_CHANNEL: f64 = 1e-12;

#[derive(Clone, Debug)]
pub struct SaturationReport {
    /// Same-index flags as the measured array (never shifted).
    pub flags: Vec<bool>,
    pub names: Vec<String>,
}

pub fn is_saturated(rgb: Vec3, threshold: f64) -> bool {
    rgb[0] >= threshold || rgb[1] >= threshold || rgb[2] >= threshold
}

pub fn flag_saturated(patches: &[ChartPatch], threshold: f64) -> SaturationReport {
    let flags: Vec<bool> = patches
        .iter()
        .map(|p| is_saturated(p.original_measured_srgb, threshold))
        .collect();
    let names = patches
        .iter()
        .zip(flags.iter())
        .filter(|(_, sat)| **sat)
        .map(|(p, _)| p.name.clone())
        .collect();
    SaturationReport { flags, names }
}

fn matching_named<'a>(
    patches: &'a [ChartPatch],
    index: usize,
    name: &'a str,
) -> impl Iterator<Item = &'a ChartPatch> + 'a {
    patches
        .iter()
        .filter(move |p| p.index == index && p.name.eq_ignore_ascii_case(name))
}

fn median_vec3(samples: impl Iterator<Item = Vec3>) -> Option<Vec3> {
    let mut rs = Vec::new();
    let mut gs = Vec::new();
    let mut bs = Vec::new();
    for s in samples {
        if s.iter().all(|c| c.is_finite()) {
            rs.push(s[0]);
            gs.push(s[1]);
            bs.push(s[2]);
        }
    }
    if rs.is_empty() {
        None
    } else {
        Some([median(rs), median(gs), median(bs)])
    }
}

/// Same-type / same-name neutrals only. Passport use-all has two faces with
/// the same index; mixed chart types stay out because the name does not match.
fn pooled_measured_srgb(patches: &[ChartPatch], index: usize, name: &str) -> Option<Vec3> {
    median_vec3(matching_named(patches, index, name).map(|p| p.original_measured_srgb))
}

fn pooled_measured_l(patches: &[ChartPatch], index: usize, name: &str) -> Option<f64> {
    let mut values = Vec::new();
    for patch in matching_named(patches, index, name) {
        if let Ok(lab) = srgb_to_lab_d65(patch.original_measured_srgb) {
            if lab.l.is_finite() {
                values.push(lab.l);
            }
        }
    }
    if values.is_empty() {
        None
    } else {
        Some(median(values))
    }
}

fn median(mut values: Vec<f64>) -> f64 {
    if values.is_empty() {
        return 0.0;
    }
    values.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let mid = values.len() / 2;
    if values.len().is_multiple_of(2) {
        (values[mid - 1] + values[mid]) * 0.5
    } else {
        values[mid]
    }
}

/// Keep a usable working range. Invalid pairs fall back to CCP_rs `[-0.2, 1.3]`.
pub fn sanitize_clip_range(lo: f64, hi: f64) -> (f64, f64) {
    let lo = if lo.is_finite() {
        lo.clamp(-2.0, 0.99)
    } else {
        RGB_CLIP_LO
    };
    let hi = if hi.is_finite() {
        hi.clamp(0.01, 8.0)
    } else {
        RGB_CLIP_HI
    };
    if hi > lo {
        (lo, hi)
    } else {
        (RGB_CLIP_LO, RGB_CLIP_HI)
    }
}

/// Optional median-scale extrapolate. Flags stay on the original measured index.
/// ΔE must use `original_measured_srgb`, never the scaled working copy.
/// Recovered channels are clamped to `[clip_lo, clip_hi]` (CCP_rs sat-check used -0.2…1.3).
pub fn extrapolate_saturated(
    patches: &mut [ChartPatch],
    threshold: f64,
    clip_lo: f64,
    clip_hi: f64,
) -> SaturationReport {
    let report = flag_saturated(patches, threshold);
    let (lo, hi) = sanitize_clip_range(clip_lo, clip_hi);
    let mut ratios = [Vec::new(), Vec::new(), Vec::new()];
    for (patch, &sat) in patches.iter().zip(report.flags.iter()) {
        if sat {
            continue;
        }
        for channel in 0..3 {
            let measured = patch.original_measured_srgb[channel];
            if measured.abs() > MIN_CHANNEL {
                ratios[channel].push(patch.reference_srgb[channel] / measured);
            }
        }
    }
    if report.names.is_empty() {
        return report;
    }
    let mut scale = [1.0; 3];
    for channel in 0..3 {
        if !ratios[channel].is_empty() {
            let s = median(ratios[channel].clone());
            scale[channel] = if s.is_finite() && s > 0.0 { s } else { 1.0 };
        }
    }
    for (patch, &sat) in patches.iter_mut().zip(report.flags.iter()) {
        patch.saturated = sat;
        if !sat {
            continue;
        }
        for channel in 0..3 {
            patch.measured_srgb[channel] =
                (patch.original_measured_srgb[channel] * scale[channel]).clamp(lo, hi);
        }
    }
    report
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GammaModel {
    /// Legacy descending polynomial coefficients `[a_n, …, a_0]` for L*.
    /// Kept so version-1 model files apply identically.
    #[serde(default)]
    pub coeffs: Vec<f64>,
    /// Monotonic Hermite knots for version-2 models.
    #[serde(default)]
    pub knots: Vec<f64>,
    #[serde(default)]
    pub values: Vec<f64>,
    #[serde(default)]
    pub slopes: Vec<f64>,
}

impl GammaModel {
    pub fn legacy(coeffs: Vec<f64>) -> Self {
        Self {
            coeffs,
            knots: Vec::new(),
            values: Vec::new(),
            slopes: Vec::new(),
        }
    }

    pub fn is_monotonic_curve(&self) -> bool {
        self.knots.len() >= 2
            && self.knots.len() == self.values.len()
            && self.knots.len() == self.slopes.len()
    }

    /// Evaluate the fitted lightness mapping. New models use a shape-preserving
    /// cubic Hermite curve; legacy models retain their polynomial behavior.
    pub fn map_lstar(&self, input: f64) -> f64 {
        let x = input.clamp(0.0, 100.0);
        if !self.is_monotonic_curve() {
            return poly_eval(x, &self.coeffs);
        }
        let last = self.knots.len() - 1;
        if x <= self.knots[0] {
            return self.values[0];
        }
        if x >= self.knots[last] {
            return self.values[last];
        }
        let upper = self.knots.partition_point(|&k| k <= x).min(last);
        let lower = upper - 1;
        let x0 = self.knots[lower];
        let x1 = self.knots[upper];
        let h = (x1 - x0).max(f64::EPSILON);
        let t = (x - x0) / h;
        let t2 = t * t;
        let t3 = t2 * t;
        let h00 = 2.0 * t3 - 3.0 * t2 + 1.0;
        let h10 = t3 - 2.0 * t2 + t;
        let h01 = -2.0 * t3 + 3.0 * t2;
        let h11 = t3 - t2;
        h00 * self.values[lower]
            + h10 * h * self.slopes[lower]
            + h01 * self.values[upper]
            + h11 * h * self.slopes[upper]
    }
}

pub fn poly_eval(x: f64, coeffs: &[f64]) -> f64 {
    let mut result = 0.0;
    for &c in coeffs {
        result = result * x + c;
    }
    result
}

#[cfg(test)]
fn polyfit(x: &[f64], y: &[f64], degree: usize) -> Result<Vec<f64>> {
    let n = x.len();
    let cols = degree + 1;
    let mut a = vec![0.0; n * cols];
    let mut b = vec![0.0; n * 3];
    for i in 0..n {
        for j in 0..cols {
            a[i * cols + j] = x[i].powi((cols - 1 - j) as i32);
        }
        b[i * 3] = y[i];
    }
    let coeffs = fit_least_squares(&a, &b, n, cols, 1e-8)?;
    Ok((0..cols).map(|j| coeffs[j * 3]).collect())
}

#[cfg(test)]
fn estimate_gamma_profile(
    ref_l: &[f64],
    measured_l: &[f64],
    max_degree: usize,
) -> Result<Vec<f64>> {
    let n = measured_l.len();
    let mut meas_aug = vec![0.0; n + 2];
    let mut ref_aug = vec![0.0; n + 2];
    meas_aug[0] = 100.0;
    ref_aug[0] = 100.0;
    meas_aug[1..n + 1].copy_from_slice(measured_l);
    ref_aug[1..n + 1].copy_from_slice(ref_l);
    // Vandermonde of degree ≥ N is singular. Classic 24 has ~6 neutrals + 2 anchors.
    let max_fit = max_degree.max(1).min(meas_aug.len().saturating_sub(1));
    let mut all_coeffs = Vec::new();
    let mut mse_values = Vec::new();
    for degree in 1..=max_fit {
        let coeffs = polyfit(&meas_aug, &ref_aug, degree)?;
        let mse: f64 = measured_l
            .iter()
            .zip(ref_l.iter())
            .map(|(&m, &r)| {
                let p = poly_eval(m, &coeffs);
                (r - p).powi(2)
            })
            .sum::<f64>()
            / n.max(1) as f64;
        all_coeffs.push(coeffs);
        mse_values.push(mse);
    }
    let mut grads = vec![0.0; mse_values.len()];
    if mse_values.len() == 1 {
        return Ok(all_coeffs.remove(0));
    }
    grads[0] = mse_values[1] - mse_values[0];
    let last = mse_values.len() - 1;
    grads[last] = mse_values[last] - mse_values[last - 1];
    for i in 1..last {
        grads[i] = (mse_values[i + 1] - mse_values[i - 1]) / 2.0;
    }
    let mut min_idx = grads
        .iter()
        .enumerate()
        .min_by(|a, b| a.1.partial_cmp(b.1).unwrap_or(std::cmp::Ordering::Equal))
        .map(|(i, _)| i)
        .unwrap_or(0);
    loop {
        if min_idx == 0 {
            break;
        }
        let max_before = grads[..min_idx]
            .iter()
            .copied()
            .fold(f64::NEG_INFINITY, f64::max);
        if max_before - grads[min_idx] < 1.7 {
            min_idx = grads[..min_idx]
                .iter()
                .enumerate()
                .min_by(|a, b| a.1.partial_cmp(b.1).unwrap_or(std::cmp::Ordering::Equal))
                .map(|(i, _)| i)
                .unwrap_or(0);
        } else {
            break;
        }
    }
    Ok(all_coeffs.remove(min_idx))
}

fn isotonic_non_decreasing(values: &mut [f64], weights: &[f64]) {
    let mut level = Vec::with_capacity(values.len());
    let mut weight = Vec::with_capacity(values.len());
    let mut start = Vec::with_capacity(values.len());
    let mut end = Vec::with_capacity(values.len());
    for (index, (&value, &w)) in values.iter().zip(weights.iter()).enumerate() {
        level.push(value);
        weight.push(w.max(f64::EPSILON));
        start.push(index);
        end.push(index + 1);
        while level.len() >= 2 {
            let n = level.len();
            if level[n - 2] <= level[n - 1] {
                break;
            }
            let merged_weight = weight[n - 2] + weight[n - 1];
            let merged_level =
                (level[n - 2] * weight[n - 2] + level[n - 1] * weight[n - 1]) / merged_weight;
            level[n - 2] = merged_level;
            weight[n - 2] = merged_weight;
            end[n - 2] = end[n - 1];
            level.pop();
            weight.pop();
            start.pop();
            end.pop();
        }
    }
    for block in 0..level.len() {
        for value in &mut values[start[block]..end[block]] {
            *value = level[block];
        }
    }
}

fn monotone_slopes(x: &[f64], y: &[f64]) -> Vec<f64> {
    let n = x.len();
    if n == 2 {
        let d = (y[1] - y[0]) / (x[1] - x[0]);
        return vec![d, d];
    }
    let h: Vec<f64> = x.windows(2).map(|p| p[1] - p[0]).collect();
    let delta: Vec<f64> = y
        .windows(2)
        .zip(h.iter())
        .map(|(p, &width)| (p[1] - p[0]) / width)
        .collect();
    let mut slopes = vec![0.0; n];
    for i in 1..n - 1 {
        if delta[i - 1] > 0.0 && delta[i] > 0.0 {
            let w1 = 2.0 * h[i] + h[i - 1];
            let w2 = h[i] + 2.0 * h[i - 1];
            slopes[i] = (w1 + w2) / (w1 / delta[i - 1] + w2 / delta[i]);
        }
    }
    slopes[0] = ((2.0 * h[0] + h[1]) * delta[0] - h[0] * delta[1]) / (h[0] + h[1]);
    if slopes[0].signum() != delta[0].signum() {
        slopes[0] = 0.0;
    } else if delta[0].signum() != delta[1].signum() && slopes[0].abs() > 3.0 * delta[0].abs() {
        slopes[0] = 3.0 * delta[0];
    }
    let j = n - 1;
    slopes[j] = ((2.0 * h[j - 1] + h[j - 2]) * delta[j - 1] - h[j - 1] * delta[j - 2])
        / (h[j - 1] + h[j - 2]);
    if slopes[j].signum() != delta[j - 1].signum() {
        slopes[j] = 0.0;
    } else if delta[j - 1].signum() != delta[j - 2].signum()
        && slopes[j].abs() > 3.0 * delta[j - 1].abs()
    {
        slopes[j] = 3.0 * delta[j - 1];
    }
    slopes
}

fn fit_monotonic_gamma_profile(ref_l: &[f64], measured_l: &[f64]) -> Result<GammaModel> {
    let mut pairs: Vec<(f64, f64)> = measured_l
        .iter()
        .zip(ref_l.iter())
        .filter_map(|(&x, &y)| {
            (x.is_finite() && y.is_finite()).then_some((x.clamp(0.0, 100.0), y.clamp(0.0, 100.0)))
        })
        .collect();
    pairs.push((0.0, 0.0));
    pairs.push((100.0, 100.0));
    pairs.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap_or(std::cmp::Ordering::Equal));

    let mut knots = Vec::new();
    let mut values = Vec::new();
    let mut counts = Vec::new();
    for (x, y) in pairs {
        if knots
            .last()
            .is_some_and(|last: &f64| (x - *last).abs() < 1e-7)
        {
            let index = values.len() - 1;
            values[index] += y;
            counts[index] += 1.0;
        } else {
            knots.push(x);
            values.push(y);
            counts.push(1.0);
        }
    }
    for (value, count) in values.iter_mut().zip(counts.iter()) {
        *value /= *count;
    }
    if knots.len() < 2 {
        return Err(Error::InvalidArgument(
            "GC needs two distinct lightness samples".into(),
        ));
    }
    // Endpoints are calibration constraints, not noisy observations.
    values[0] = 0.0;
    let last = values.len() - 1;
    values[last] = 100.0;
    let mut weights = vec![1.0; values.len()];
    weights[0] = 1e9;
    weights[last] = 1e9;
    isotonic_non_decreasing(&mut values, &weights);
    values[0] = 0.0;
    values[last] = 100.0;
    let slopes = monotone_slopes(&knots, &values);
    Ok(GammaModel {
        coeffs: Vec::new(),
        knots,
        values,
        slopes,
    })
}

/// Fit GC from **named** neutrals only (that chart’s table).
pub fn fit_gc(
    spec: &ChartSpec,
    patches: &[ChartPatch],
    neutrals: Option<&HashMap<String, Vec<String>>>,
) -> Result<GammaModel> {
    let named = lookup_neutrals(spec, neutrals)?;
    let d65 = parse_white_point("d65")?;
    let mut ref_l = Vec::new();
    let mut meas_l = Vec::new();
    for (index, name, lab) in named {
        let Some(meas) = pooled_measured_l(patches, index, &name) else {
            continue;
        };
        let ref_adapted = lab_d50_to_target_lab(lab, &d65)?;
        ref_l.push(ref_adapted.l);
        meas_l.push(meas);
    }
    if meas_l.len() < 2 {
        return Err(Error::InvalidArgument(
            "GC needs at least two named neutrals".into(),
        ));
    }
    fit_monotonic_gamma_profile(&ref_l, &meas_l)
}

fn apply_gc_pixel_cached(
    rgb: Vec3,
    model: &GammaModel,
    d65: &WhitePoint,
    space: &RgbColorSpace,
) -> Vec3 {
    let lab = crate::color::xyz_to_lab(
        crate::color::linear_rgb_to_xyz(srgb_to_linear(rgb), space),
        d65,
    );
    let l = {
        let v = model.map_lstar(lab.l);
        if v.is_finite() {
            v.clamp(0.0, 100.0)
        } else {
            lab.l.clamp(0.0, 100.0)
        }
    };
    let xyz = lab_to_xyz(
        Lab {
            l,
            a: lab.a,
            b: lab.b,
        },
        d65,
    );
    crate::color::clamp01_vec(linear_to_srgb(xyz_to_linear_rgb(xyz, space)))
}

pub fn apply_gc_pixel(rgb: Vec3, model: &GammaModel) -> Result<Vec3> {
    let (d65, space) = srgb_d65_space();
    Ok(apply_gc_pixel_cached(rgb, model, d65, space))
}

pub fn apply_gc_inplace(img: &mut Array3<f32>, model: &GammaModel) -> Result<()> {
    let (d65, space) = srgb_d65_space();
    map_rgb_inplace(img, |rgb| {
        let c = apply_gc_pixel_cached(rgb, model, d65, space);
        [c[0] as f32, c[1] as f32, c[2] as f32]
    });
    Ok(())
}

pub fn apply_gc_image(img: &Array3<f32>, model: &GammaModel) -> Result<Array3<f32>> {
    let mut out = img.clone();
    apply_gc_inplace(&mut out, model)?;
    Ok(out)
}

/// CCP `compute_diag`: per-channel median(ref/det) × 0.95. No grey-world.
pub fn compute_diag(mat_ref: &[Vec3], mat_det: &[Vec3], rf: f64) -> Result<[f64; 3]> {
    if mat_ref.len() != mat_det.len() {
        return Err(Error::InvalidArgument(
            "WB ref and detected neutrals must have the same count".into(),
        ));
    }
    let mut factors = [1.0; 3];
    let mut any = false;
    for c in 0..3 {
        let mut ratios = Vec::new();
        for (r, d) in mat_ref.iter().zip(mat_det.iter()) {
            if d[c].abs() > MIN_CHANNEL && r[c].is_finite() && d[c].is_finite() {
                ratios.push(r[c] / d[c]);
            }
        }
        if !ratios.is_empty() {
            any = true;
            factors[c] = rf * median(ratios);
        }
    }
    if !any {
        return Err(Error::InvalidArgument(
            "WB needs at least one usable channel".into(),
        ));
    }
    Ok(factors)
}

#[derive(Clone, Debug)]
pub struct WhiteBalance {
    pub diag: [f64; 3],
}

/// Diagonal from named neutrals vs adapted reference RGB. Scale 0.95.
pub fn fit_wb(
    spec: &ChartSpec,
    patches: &[ChartPatch],
    neutrals: Option<&HashMap<String, Vec<String>>>,
) -> Result<WhiteBalance> {
    let named = lookup_neutrals(spec, neutrals)?;
    let mut refs = Vec::new();
    let mut dets = Vec::new();
    for (index, name, lab) in named {
        let Some(det) = pooled_measured_srgb(patches, index, &name) else {
            continue;
        };
        refs.push(lab_d50_to_srgb(lab)?);
        dets.push(det);
    }
    if refs.len() < 2 {
        return Err(Error::InvalidArgument(
            "WB needs at least two named neutrals".into(),
        ));
    }
    Ok(WhiteBalance {
        diag: compute_diag(&refs, &dets, WB_DIAG_SCALE)?,
    })
}

pub fn apply_wb_pixel(rgb: Vec3, wb: &WhiteBalance) -> Vec3 {
    crate::color::clamp01_vec([
        rgb[0] * wb.diag[0],
        rgb[1] * wb.diag[1],
        rgb[2] * wb.diag[2],
    ])
}

pub fn apply_wb_inplace(img: &mut Array3<f32>, wb: &WhiteBalance) {
    map_rgb_inplace(img, |rgb| {
        let c = apply_wb_pixel(rgb, wb);
        [c[0] as f32, c[1] as f32, c[2] as f32]
    });
}

pub fn apply_wb_image(img: &Array3<f32>, wb: &WhiteBalance) -> Array3<f32> {
    let mut out = img.clone();
    apply_wb_inplace(&mut out, wb);
    out
}

pub fn apply_encoded_lut_inplace(img: &mut Array3<f32>, lut: &[f32], gs: usize) {
    map_rgb_inplace(img, |rgb| {
        let c = crate::maps::sample_encoded_lut(lut, gs, rgb);
        [c[0] as f32, c[1] as f32, c[2] as f32]
    });
}

pub fn apply_cc_inplace(
    img: &mut Array3<f32>,
    model: &CorrectionModel,
    settings: &CorrectionSettings,
) -> Result<()> {
    let pixels = img.len() / 3;
    if pixels >= crate::maps::APPLY_LUT_MIN_PIXELS {
        let gs = crate::maps::APPLY_LUT_SIZE;
        let lut = crate::maps::build_encoded_lut(model, settings, gs)?;
        apply_encoded_lut_inplace(img, &lut, gs);
        return Ok(());
    }
    try_map_rgb_inplace(img, |rgb| {
        crate::maps::apply_correction_pixel(model, rgb, settings)
            .map(|c| [c[0] as f32, c[1] as f32, c[2] as f32])
    })
}

pub fn apply_cc_image(
    img: &Array3<f32>,
    model: &CorrectionModel,
    settings: &CorrectionSettings,
) -> Result<Array3<f32>> {
    let mut out = img.clone();
    apply_cc_inplace(&mut out, model, settings)?;
    Ok(out)
}

fn ensure_contiguous(img: &mut Array3<f32>) {
    if img.as_slice_mut().is_none() {
        let dim = img.raw_dim();
        let owned: Vec<f32> = img.iter().copied().collect();
        *img = Array3::from_shape_vec(dim, owned).expect("contiguous HxWx3");
    }
}

fn map_rgb_inplace(img: &mut Array3<f32>, f: impl Fn(Vec3) -> [f32; 3] + Sync) {
    ensure_contiguous(img);
    let slice = img.as_slice_mut().expect("contiguous HxWx3");
    slice.par_chunks_exact_mut(3).for_each(|px| {
        let mapped = f([px[0] as f64, px[1] as f64, px[2] as f64]);
        px.copy_from_slice(&mapped);
    });
}

fn try_map_rgb_inplace(
    img: &mut Array3<f32>,
    f: impl Fn(Vec3) -> Result<[f32; 3]> + Sync,
) -> Result<()> {
    ensure_contiguous(img);
    let slice = img.as_slice_mut().expect("contiguous HxWx3");
    slice.par_chunks_exact_mut(3).try_for_each(|px| {
        let mapped = f([px[0] as f64, px[1] as f64, px[2] as f64])?;
        px.copy_from_slice(&mapped);
        Ok::<(), Error>(())
    })
}

#[derive(Clone, Debug, Default)]
pub struct StageConfig {
    pub do_sat_extrapolate: bool,
    pub do_gc: bool,
    pub do_wb: bool,
    pub do_cc: bool,
}

impl StageConfig {
    pub fn default_on() -> Self {
        Self {
            do_sat_extrapolate: true,
            do_gc: true,
            do_wb: true,
            do_cc: true,
        }
    }
}

pub fn default_sat_threshold() -> f64 {
    SATURATION_THRESHOLD
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn gamma_max_degree_nine_does_not_exceed_sample_rank() {
        let meas = [12.0, 28.0, 46.0, 62.0, 78.0, 91.0];
        let refs = [14.0, 30.0, 48.0, 64.0, 80.0, 93.0];
        let coeffs = estimate_gamma_profile(&refs, &meas, 9).unwrap();
        assert!(coeffs.len() >= 2);
        assert!(coeffs.len() <= meas.len() + 2);
        for &c in &coeffs {
            assert!(
                c.is_finite(),
                "GC coeffs must stay finite at requested degree 9"
            );
        }
        let y = poly_eval(46.0, &coeffs);
        assert!(y.is_finite());
    }

    #[test]
    fn monotonic_gamma_is_anchored_and_never_reverses() {
        // Deliberately noisy references include a local reversal.
        let meas = [8.0, 22.0, 44.0, 67.0, 86.0, 96.0];
        let refs = [10.0, 31.0, 49.0, 72.0, 70.0, 94.0];
        let model = fit_monotonic_gamma_profile(&refs, &meas).unwrap();
        assert!(model.is_monotonic_curve());
        assert!(model.map_lstar(0.0).abs() < 1e-12);
        assert!((model.map_lstar(100.0) - 100.0).abs() < 1e-12);
        let mut previous = model.map_lstar(0.0);
        for step in 1..=10_000 {
            let current = model.map_lstar(step as f64 / 100.0);
            assert!(current.is_finite());
            assert!((0.0..=100.0).contains(&current));
            assert!(
                current + 1e-9 >= previous,
                "GC reversed at L*={}: {previous} -> {current}",
                step as f64 / 100.0
            );
            previous = current;
        }
    }

    #[test]
    fn legacy_gamma_still_uses_polynomial() {
        let model = GammaModel::legacy(vec![1.05, -2.0]);
        assert!((model.map_lstar(50.0) - 50.5).abs() < 1e-12);
    }

    fn test_patch(spec: &ChartSpec, name: &str, rgb: Vec3) -> ChartPatch {
        let index = spec.index_of(name).expect("named patch");
        ChartPatch {
            name: spec.names[index].clone(),
            index,
            reference_lab_d50: spec.reference_lab_d50[index],
            reference_srgb: spec.reference_srgb[index],
            measured_srgb: rgb,
            original_measured_srgb: rgb,
            center: crate::refs::Vec2 { x: 0.0, y: 0.0 },
            saturated: false,
        }
    }

    #[test]
    fn use_all_pools_same_name_neutrals_for_wb() {
        let spec = crate::refs::chart_spec(crate::refs::ChartKind::Classic).unwrap();
        let white_lo = [0.20, 0.20, 0.20];
        let white_hi = [0.60, 0.60, 0.60];
        let black_lo = [0.05, 0.05, 0.05];
        let black_hi = [0.15, 0.15, 0.15];
        let mut patches = vec![
            test_patch(&spec, "white", white_lo),
            test_patch(&spec, "white", white_hi),
            test_patch(&spec, "black", black_lo),
            test_patch(&spec, "black", black_hi),
        ];
        patches.push(ChartPatch {
            name: "A1".into(),
            index: spec.index_of("white").unwrap(),
            reference_lab_d50: spec.reference_lab_d50[0],
            reference_srgb: spec.reference_srgb[0],
            measured_srgb: [0.01, 0.99, 0.01],
            original_measured_srgb: [0.01, 0.99, 0.01],
            center: crate::refs::Vec2 { x: 0.0, y: 0.0 },
            saturated: false,
        });

        let wb = fit_wb(&spec, &patches, None).unwrap();
        let expected = compute_diag(
            &[
                lab_d50_to_srgb(spec.reference_lab_d50[spec.index_of("white").unwrap()]).unwrap(),
                lab_d50_to_srgb(spec.reference_lab_d50[spec.index_of("black").unwrap()]).unwrap(),
            ],
            &[[0.40, 0.40, 0.40], [0.10, 0.10, 0.10]],
            WB_DIAG_SCALE,
        )
        .unwrap();
        for i in 0..3 {
            assert!(
                (wb.diag[i] - expected[i]).abs() < 1e-9,
                "channel {i}: {} vs {}",
                wb.diag[i],
                expected[i]
            );
        }
        let first_only = compute_diag(
            &[
                lab_d50_to_srgb(spec.reference_lab_d50[spec.index_of("white").unwrap()]).unwrap(),
                lab_d50_to_srgb(spec.reference_lab_d50[spec.index_of("black").unwrap()]).unwrap(),
            ],
            &[white_lo, black_lo],
            WB_DIAG_SCALE,
        )
        .unwrap();
        assert!(
            (wb.diag[0] - first_only[0]).abs() > 1e-4,
            "pooling must not match first-face-only WB"
        );
    }

    #[test]
    fn use_all_pools_same_name_neutrals_for_gc() {
        let spec = crate::refs::chart_spec(crate::refs::ChartKind::Classic).unwrap();
        let patches = vec![
            test_patch(&spec, "white", [0.20, 0.20, 0.20]),
            test_patch(&spec, "white", [0.60, 0.60, 0.60]),
            test_patch(&spec, "black", [0.05, 0.05, 0.05]),
            test_patch(&spec, "black", [0.15, 0.15, 0.15]),
        ];
        let gc = fit_gc(&spec, &patches, None).unwrap();
        assert!(gc.is_monotonic_curve());
        assert!(gc.slopes.iter().all(|s| s.is_finite()));

        let first_only = vec![
            test_patch(&spec, "white", [0.20, 0.20, 0.20]),
            test_patch(&spec, "black", [0.05, 0.05, 0.05]),
        ];
        let gc_first = fit_gc(&spec, &first_only, None).unwrap();
        let drift: f64 = (0..=100)
            .map(|l| (gc.map_lstar(l as f64) - gc_first.map_lstar(l as f64)).abs())
            .sum();
        assert!(
            drift > 1e-6,
            "pooled GC must differ from first-face-only ({drift})"
        );
    }

    #[test]
    fn wb_diag_keeps_identity_on_missing_channel() {
        let refs = [[0.5, 0.5, 0.5], [0.4, 0.4, 0.4]];
        let dets = [[0.5, 0.5, 0.0], [0.4, 0.4, 0.0]];
        let diag = compute_diag(&refs, &dets, 0.95).unwrap();
        assert!((diag[0] - 0.95).abs() < 1e-12);
        assert!((diag[1] - 0.95).abs() < 1e-12);
        assert!(
            (diag[2] - 1.0).abs() < 1e-12,
            "missing blue must stay 1.0, got {}",
            diag[2]
        );
    }

    #[test]
    fn wb_diag_errors_when_every_channel_is_empty() {
        let refs = [[0.5, 0.5, 0.5]];
        let dets = [[0.0, 0.0, 0.0]];
        assert!(compute_diag(&refs, &dets, 0.95).is_err());
    }

    fn sat_patch(index: usize, name: &str, rgb: Vec3) -> ChartPatch {
        ChartPatch {
            name: name.into(),
            index,
            reference_lab_d50: crate::color::Lab::new(50.0, 0.0, 0.0),
            reference_srgb: [0.8, 0.8, 0.8],
            measured_srgb: rgb,
            original_measured_srgb: rgb,
            center: crate::refs::Vec2 { x: 0.0, y: 0.0 },
            saturated: rgb.iter().any(|&c| c >= SATURATION_THRESHOLD),
        }
    }

    #[test]
    fn extrapolate_clamps_recovered_rgb_to_working_limits() {
        let mut patches = vec![
            sat_patch(0, "grey", [0.40, 0.40, 0.40]),
            sat_patch(1, "white", [1.0, 0.99, 0.99]),
        ];
        patches[0].reference_srgb = [0.80, 0.80, 0.80];
        patches[1].reference_srgb = [0.90, 0.90, 0.90];
        let _ = extrapolate_saturated(&mut patches, SATURATION_THRESHOLD, -0.2, 1.3);
        assert!(patches[1].saturated);
        assert!(
            patches[1].measured_srgb.iter().all(|&c| c <= 1.3 + 1e-12),
            "recovered sat patch must not exceed clip_hi: {:?}",
            patches[1].measured_srgb
        );
        assert!(
            patches[1].measured_srgb[0] > patches[1].original_measured_srgb[0],
            "extrapolate should still lift the clipped channel"
        );
        assert_eq!(patches[1].original_measured_srgb[0], 1.0);
    }
}
