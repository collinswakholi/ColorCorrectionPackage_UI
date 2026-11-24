//! Phase 2 teacher: emit RPCC2 weights.

use crate::maps::{fit_correction_model, CorrectionSettings, MappingOptions, ModelKind};
use crate::refs::ChartPatch;
use crate::{Result, RIDGE_LAMBDA};
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TeacherOutput {
    pub kind: String,
    pub feature_count: i32,
    pub coefficients: Vec<f64>,
}

/// Fit RPCC2 from same-index measured + reference. Teacher only.
pub fn emit_rpcc2_teacher(
    patches: &[ChartPatch],
    settings: Option<&CorrectionSettings>,
) -> Result<TeacherOutput> {
    let owned = if settings.is_none() {
        Some(CorrectionSettings::srgb_d65()?)
    } else {
        None
    };
    let settings = settings.unwrap_or_else(|| owned.as_ref().unwrap());
    let model = fit_correction_model(
        patches,
        ModelKind::RootPolynomial2,
        settings,
        RIDGE_LAMBDA,
        &MappingOptions::default(),
    )?;
    Ok(TeacherOutput {
        kind: ModelKind::RootPolynomial2.name().into(),
        feature_count: model.feature_count,
        coefficients: model.coefficients,
    })
}
