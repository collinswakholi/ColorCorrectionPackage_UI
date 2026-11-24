//! Honest ΔE00: same-index cells only.

use crate::color::{
    adapt_xyz_white, delta_e_2000, encoded_to_linear_rgb, lab_d50_to_target_lab, linear_rgb_to_xyz,
    parse_white_point, srgb_to_lab_d65, xyz_to_lab, Lab, RgbColorSpace, Vec3,
};
use crate::error::{Error, Result};
use crate::maps::{
    apply_correction_pixel, fit_correction_model, CorrectionSettings, MappingOptions, ModelKind,
};
use crate::refs::ChartPatch;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CrossValidationReport {
    pub folds: usize,
    pub samples: usize,
    pub mean: f64,
    pub median: f64,
    pub max: f64,
}

/// Deterministic outer-fold validation. Every reported patch is predicted by a
/// model that did not train on that patch index. Repeated captures of the same
/// chart cell stay in the same fold to avoid cross-capture leakage.
pub fn cross_validate_correction(
    patches: &[ChartPatch],
    kind: ModelKind,
    settings: &CorrectionSettings,
    ridge: f64,
    mapping: &MappingOptions,
) -> Result<CrossValidationReport> {
    if patches.len() < 4 {
        return Err(Error::InvalidArgument(
            "cross-validation needs at least four chart patches".into(),
        ));
    }
    let folds = 5.min(patches.len());
    let fold_values: Result<Vec<Vec<f64>>> = (0..folds)
        .into_par_iter()
        .map(|fold| {
            let train: Vec<ChartPatch> = patches
                .iter()
                .filter(|patch| patch.index % folds != fold)
                .cloned()
                .collect();
            let held: Vec<&ChartPatch> = patches
                .iter()
                .filter(|patch| patch.index % folds == fold)
                .collect();
            if held.is_empty() || train.len() < 3 {
                return Ok(Vec::new());
            }
            let model = fit_correction_model(&train, kind, settings, ridge, mapping)?;
            let mut values = Vec::with_capacity(held.len());
            for patch in held {
                let corrected = apply_correction_pixel(&model, patch.measured_srgb, settings)?;
                let de =
                    delta_e00_in_space(corrected, patch.reference_lab_d50, &settings.target_space)?;
                if de.is_finite() {
                    values.push(de);
                }
            }
            Ok(values)
        })
        .collect();
    let values: Vec<f64> = fold_values?.into_iter().flatten().collect();
    if values.is_empty() {
        return Err(Error::InvalidArgument(
            "cross-validation produced no held-out predictions".into(),
        ));
    }
    let mean = values.iter().sum::<f64>() / values.len() as f64;
    let max = values.iter().copied().fold(0.0, f64::max);
    let med = median(values.clone());
    Ok(CrossValidationReport {
        folds,
        samples: values.len(),
        mean,
        median: med,
        max,
    })
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

/// Measured encoded sRGB vs that cell's Lab D50, both in D65 Lab.
/// Compatibility helper; use `delta_e00_in_space` for selected input/output spaces.
pub fn delta_e00_same_index(measured_srgb: Vec3, reference_lab_d50: Lab) -> Result<f64> {
    let d65 = parse_white_point("d65")?;
    let meas = srgb_to_lab_d65(measured_srgb)?;
    let reference = lab_d50_to_target_lab(reference_lab_d50, &d65)?;
    Ok(delta_e_2000(meas, reference))
}

/// Compare RGB in its declared encoding against the same-index reference.
/// Both colors are Bradford-adapted to a common D65 Lab evaluation white,
/// so changing output space does not change the comparison illuminant.
pub fn delta_e00_in_space(rgb: Vec3, reference_lab_d50: Lab, space: &RgbColorSpace) -> Result<f64> {
    let d65 = parse_white_point("d65")?;
    let xyz = linear_rgb_to_xyz(encoded_to_linear_rgb(rgb, space), space);
    let measured = xyz_to_lab(adapt_xyz_white(xyz, &space.white, &d65)?, &d65);
    let reference = lab_d50_to_target_lab(reference_lab_d50, &d65)?;
    Ok(delta_e_2000(measured, reference))
}

pub fn mean_delta_e00(measured_srgb: &[Vec3], reference_lab_d50: &[Lab]) -> Result<f64> {
    if measured_srgb.len() != reference_lab_d50.len() || measured_srgb.is_empty() {
        return Err(Error::InvalidArgument(
            "ΔE00 requires same-length measured and reference arrays".into(),
        ));
    }
    let mut sum = 0.0;
    for (rgb, lab) in measured_srgb.iter().zip(reference_lab_d50.iter()) {
        sum += delta_e00_same_index(*rgb, *lab)?;
    }
    Ok(sum / measured_srgb.len() as f64)
}
