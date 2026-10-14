//! Phase 2 teacher: emit RPCC2 weights. Reconciler stays off unless a `.ts` exists.

use crate::maps::{
    fit_correction_model, CorrectionModel, CorrectionSettings, MappingOptions, ModelKind,
};
use crate::refs::ChartPatch;
use crate::{Error, Result, RIDGE_LAMBDA};
use serde::{Deserialize, Serialize};
use std::path::Path;

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

pub fn teacher_model(
    patches: &[ChartPatch],
    settings: &CorrectionSettings,
) -> Result<CorrectionModel> {
    fit_correction_model(
        patches,
        ModelKind::RootPolynomial2,
        settings,
        RIDGE_LAMBDA,
        &MappingOptions::default(),
    )
}

/// Reconciler is disabled unless `path` exists and ends with `.ts`.
pub fn reconciler_enabled(ts_path: Option<&Path>) -> bool {
    match ts_path {
        Some(path) => {
            path.extension()
                .and_then(|e| e.to_str())
                .is_some_and(|e| e.eq_ignore_ascii_case("ts"))
                && path.is_file()
        }
        None => false,
    }
}

pub fn run_reconciler(_ts_path: Option<&Path>, _patches: &[ChartPatch]) -> Result<TeacherOutput> {
    if !reconciler_enabled(_ts_path) {
        return Err(Error::ReconcilerDisabled);
    }
    Err(Error::Runtime(
        "Phase 2 reconciler weights are not loaded in v1; teacher (rpcc2) only".into(),
    ))
}
