//! The saved calibration model: the JSON file a fit writes and apply reads.
//!
//! The struct is the file format, so its fields, their order and their serde
//! attributes must not change; behaviour that needs them lives here.

use std::path::Path;

use ccp_core::color::{parse_rgb_color_space, parse_white_point};
use ccp_core::ffc::{FfcFitMethod, FfcFitParams};
use ccp_core::lut::{write_cube, LutPipeline};
use ccp_core::maps::{CorrectionModel, CorrectionSettings, ModelKind};
use ccp_core::reference_profile::ResolvedReferenceContext;
use ccp_core::stages::{GammaModel, WhiteBalance};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::error::{Error, ErrorCode, Result};
use crate::ffc::WhiteField;
use crate::request::json_str;
use crate::work::is_srgb_d65;

#[derive(Clone, Debug, Serialize, Deserialize)]
pub(crate) struct SavedPipeline {
    version: u32,
    kind: String,
    do_ffc: bool,
    do_gc: bool,
    do_wb: bool,
    do_cc: bool,
    /// Pre-0.1 polynomial GC. Always written as null; a model that still relies
    /// on it is rejected by [`SavedPipeline::load`] with a refit message.
    gc_coeffs: Option<Vec<f64>>,
    /// Endpoint-safe monotonic lightness curve.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    gc_model: Option<GammaModel>,
    wb_diag: Option<[f64; 3]>,
    correction: Option<CorrectionModel>,
    input_space_id: String,
    input_white_id: String,
    target_space_id: String,
    target_white_id: String,
    /// Always null: the FFC gain is refitted from the white image, never saved.
    gain_map_path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    ffc_params: Option<FfcFitParams>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    ffc_plane: Option<[i32; 4]>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    ffc_plane_source: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    ffc_white_size: Option<[i32; 2]>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    reference_context: Option<ResolvedReferenceContext>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    inference: Option<Value>,
}

impl Default for SavedPipeline {
    fn default() -> Self {
        Self {
            version: 2,
            kind: ModelKind::RootPolynomial2.name().into(),
            do_ffc: false,
            do_gc: true,
            do_wb: true,
            do_cc: true,
            gc_coeffs: None,
            gc_model: None,
            wb_diag: None,
            correction: None,
            input_space_id: "srgb".into(),
            input_white_id: "d65".into(),
            target_space_id: "srgb".into(),
            target_white_id: "d65".into(),
            gain_map_path: None,
            ffc_params: None,
            ffc_plane: None,
            ffc_plane_source: None,
            ffc_white_size: None,
            reference_context: None,
            inference: None,
        }
    }
}

/// The input and target encodings and whites a request selects by id.
pub(crate) struct SpaceIds {
    input_space: String,
    input_white: String,
    target_space: String,
    target_white: String,
}

impl SpaceIds {
    /// `spaces.input`/`spaces.target` and `whites.*`, or their flat aliases.
    pub(crate) fn from_req(req: &Value) -> Self {
        let spaces = req.get("spaces").cloned().unwrap_or(Value::Null);
        let whites = req.get("whites").cloned().unwrap_or(Value::Null);
        let pick = |nested: &Value, keys: &[&str], flat: &str, default: &str| {
            json_str(nested, keys)
                .or_else(|| json_str(req, &[flat]))
                .unwrap_or(default)
                .to_string()
        };
        Self {
            input_space: pick(&spaces, &["input", "input_space"], "input_space", "srgb"),
            input_white: pick(&whites, &["input", "input_white"], "input_white", "d65"),
            target_space: pick(&spaces, &["target", "target_space"], "target_space", "srgb"),
            target_white: pick(&whites, &["target", "target_white"], "target_white", "d65"),
        }
    }

    pub(crate) fn settings(&self) -> Result<CorrectionSettings> {
        correction_settings(
            &self.input_space,
            &self.input_white,
            &self.target_space,
            &self.target_white,
        )
    }
}

pub(crate) fn correction_settings(
    input_space: &str,
    input_white: &str,
    target_space: &str,
    target_white: &str,
) -> Result<CorrectionSettings> {
    let in_white = parse_white_point(input_white)?;
    let tgt_white = parse_white_point(target_white)?;
    Ok(CorrectionSettings {
        input_space: parse_rgb_color_space(input_space, &in_white)?,
        target_space: parse_rgb_color_space(target_space, &tgt_white)?,
        target_white: tgt_white,
    })
}

/// FFC, GC and WB operate on encoded sRGB/D65 pixels.
pub(crate) fn validate_stage_input(
    settings: &CorrectionSettings,
    ffc: bool,
    gc: bool,
    wb: bool,
) -> Result<()> {
    if (ffc || gc || wb) && !is_srgb_d65(&settings.input_space) {
        return Err(Error::Message(
            "FFC, GC and WB require encoded sRGB / D65 input. Convert the source image to sRGB / D65 first, or disable FFC, GC and WB to use CC-only with another input space. Changing the selector does not convert the source pixels.".into(),
        ));
    }
    Ok(())
}

fn ffc_method_name(method: FfcFitMethod) -> &'static str {
    match method {
        FfcFitMethod::Linear => "linear",
        FfcFitMethod::Poly => "poly",
        FfcFitMethod::Pls => "pls",
        FfcFitMethod::Nn => "nn",
    }
}

impl SavedPipeline {
    /// A new fit's model before its stages are recorded. `ffc` is the white
    /// field the fit applied, if any.
    pub(crate) fn fitted(
        kind: ModelKind,
        (do_gc, do_wb, do_cc): (bool, bool, bool),
        spaces: &SpaceIds,
        ffc: Option<(&WhiteField, FfcFitParams)>,
        reference_context: Option<ResolvedReferenceContext>,
        inference: Value,
    ) -> Self {
        Self {
            kind: kind.name().into(),
            do_ffc: ffc.is_some(),
            do_gc,
            do_wb,
            do_cc,
            input_space_id: spaces.input_space.clone(),
            input_white_id: spaces.input_white.clone(),
            target_space_id: spaces.target_space.clone(),
            target_white_id: spaces.target_white.clone(),
            ffc_plane: ffc.as_ref().map(|(white, _)| white.plane()),
            ffc_plane_source: ffc.as_ref().map(|(white, _)| white.plane_source().into()),
            ffc_white_size: ffc.as_ref().map(|(white, _)| white.white_size()),
            ffc_params: ffc.map(|(_, params)| params),
            reference_context,
            inference: Some(inference),
            ..Self::default()
        }
    }

    pub(crate) fn record_gc(&mut self, gc: GammaModel) {
        self.gc_model = Some(gc);
    }

    pub(crate) fn record_wb(&mut self, wb: &WhiteBalance) {
        self.wb_diag = Some(wb.diag);
    }

    pub(crate) fn record_correction(&mut self, model: CorrectionModel) {
        self.correction = Some(model);
    }

    /// Read and validate a saved model, rejecting formats no release still reads.
    pub(crate) fn load(path: impl AsRef<Path>) -> Result<Self> {
        let path = path.as_ref();
        let text = std::fs::read_to_string(path).map_err(|error| {
            Error::coded(
                ErrorCode::IoError,
                format!("Cannot load calibration model {}: {error}. Locate the saved model or fit a new calibration.", path.display()),
            )
        })?;
        let invalid = |error: &dyn std::fmt::Display| {
            Error::coded(ErrorCode::InvalidModel, error.to_string())
        };
        let v: Value = serde_json::from_str(&text).map_err(|e| invalid(&e))?;
        let unsupported = || {
            invalid(&format!(
                "{} was written by a release older than ColorCorrect Studio 0.1 and is no longer supported. Fit a new calibration.",
                path.display()
            ))
        };
        if v.get("correction").is_none()
            && v.get("gc_coeffs").is_none()
            && v.get("gc_model").is_none()
            && v.get("wb_diag").is_none()
        {
            // Bare correction models predate the saved-pipeline format; no
            // tagged release wrote them.
            return Err(invalid(&format!(
                "Not a ColorCorrect saved pipeline: {}. Bare correction models from releases older than ColorCorrect Studio 0.1 are no longer supported. Fit a new calibration.",
                path.display()
            )));
        }
        let saved: Self = serde_json::from_value(v).map_err(|e| invalid(&e))?;
        match &saved.gc_model {
            Some(gc) if !gc.is_monotonic_curve() => return Err(unsupported()),
            None if saved.gc_coeffs.as_ref().is_some_and(|c| !c.is_empty()) => {
                return Err(unsupported())
            }
            _ => {}
        }
        if let Some(correction) = &saved.correction {
            correction.validate()?;
        }
        if let Some(context) = &saved.reference_context {
            if context.version != 1 {
                return Err(invalid(&"unsupported reference context version"));
            }
            for profile in &context.reference_profiles {
                profile.resolve().map_err(|e| invalid(&e))?;
            }
        }
        Ok(saved)
    }

    pub(crate) fn save(&self, path: &Path) -> Result<()> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(path, serde_json::to_string_pretty(self)?)?;
        Ok(())
    }

    /// The `.cube` of the global stages; it cannot encode spatial FFC.
    pub(crate) fn export_cube(&self, path: &Path, size: usize) -> Result<()> {
        write_cube(path, "ColorCorrect Studio", size, &self.stages()?)?;
        Ok(())
    }

    pub(crate) fn settings(&self) -> Result<CorrectionSettings> {
        correction_settings(
            &self.input_space_id,
            &self.input_white_id,
            &self.target_space_id,
            &self.target_white_id,
        )
    }

    /// The fitted global stages this model enables.
    pub(crate) fn stages(&self) -> Result<LutPipeline> {
        let settings = self.settings()?;
        validate_stage_input(&settings, self.do_ffc, self.do_gc, self.do_wb)?;
        Ok(LutPipeline {
            gc: self.gc_model.clone().filter(|_| self.do_gc),
            wb: self
                .wb_diag
                .filter(|_| self.do_wb)
                .map(|diag| WhiteBalance { diag }),
            cc: self
                .correction
                .clone()
                .filter(|_| self.do_cc)
                .map(|model| (model, settings)),
        })
    }

    pub(crate) fn gc_enabled(&self) -> bool {
        self.do_gc
    }

    pub(crate) fn wb_enabled(&self) -> bool {
        self.do_wb
    }

    pub(crate) fn cc_enabled(&self) -> bool {
        self.do_cc
    }

    pub(crate) fn ffc_params(&self) -> Option<&FfcFitParams> {
        self.ffc_params.as_ref()
    }

    /// Record the model's process, FFC region and diagnostics in a fit report.
    pub(crate) fn describe_in_report(&self, report: &mut Value) {
        report["inference"] = json!(self.inference);
        if self.ffc_plane_source.as_deref() == Some("full") {
            report["ffc_warning"] =
                json!("No white plane was found; the full white image was used.");
        }
        if let Some(model) = self
            .correction
            .as_ref()
            .filter(|model| model.kind == ModelKind::NeuralNet)
        {
            report["nn_diagnostics"] = json!({
                "epochs_trained": model.nn_epochs_trained,
                "best_internal_score": model.nn_best_validation_score,
                "best_internal_loss": model.nn_best_validation_loss,
                "outer_cross_validation_is_authoritative": true,
            });
        }
        if let Some(rect) = self.ffc_plane {
            report["ffc_plane"] = json!(rect);
        }
        if let Some(source) = &self.ffc_plane_source {
            report["ffc_plane_source"] = json!(source);
        }
        if let Some(size) = self.ffc_white_size {
            report["ffc_white_size"] = json!(size);
        }
        let ffc_method = self
            .ffc_params
            .as_ref()
            .map(|p| ffc_method_name(p.method))
            .unwrap_or("off");
        report["do_ffc"] = json!(self.do_ffc);
        report["do_gc"] = json!(self.do_gc);
        report["do_wb"] = json!(self.do_wb);
        report["do_cc"] = json!(self.do_cc);
        report["ffc_method"] = json!(ffc_method);
        let parts = [
            if self.do_ffc {
                format!("FFC {ffc_method}")
            } else {
                "FFC off".into()
            },
            if self.do_gc { "GC safe" } else { "GC off" }.into(),
            if self.do_wb { "WB" } else { "WB off" }.into(),
            if self.do_cc {
                format!("CC {}", self.kind)
            } else {
                "CC off".into()
            },
        ];
        report["process"] = json!(parts.join(" · "));
        match self.ffc_params.as_ref() {
            Some(p) if p.method == FfcFitMethod::Linear => {
                report["ffc_effective_degree"] = json!(1)
            }
            Some(p) if p.method == FfcFitMethod::Poly => {
                report["ffc_effective_degree"] = json!(p.degree)
            }
            _ => {}
        }
    }
}

#[cfg(test)]
impl SavedPipeline {
    /// A GC curve, WB gains and a 3x3 CCM: every global stage present.
    pub(crate) fn all_stages_fixture() -> Self {
        Self {
            do_gc: true,
            do_wb: true,
            do_cc: true,
            gc_model: Some(GammaModel::curve(
                vec![0.0, 50.0, 100.0],
                vec![0.0, 45.0, 100.0],
            )),
            wb_diag: Some([1.12, 0.91, 1.03]),
            correction: Some(CorrectionModel {
                kind: ModelKind::Ccm3x3,
                feature_count: 3,
                coefficients: vec![0.7, 0.1, 0.2, 0.2, 0.8, 0.1, 0.1, 0.1, 0.7],
                ..CorrectionModel::default()
            }),
            ..Self::default()
        }
    }

    /// CC only, with an identity 3x3 matrix.
    pub(crate) fn identity_ccm_fixture() -> Self {
        Self {
            do_gc: false,
            do_wb: false,
            correction: Some(CorrectionModel {
                kind: ModelKind::Ccm3x3,
                feature_count: 3,
                coefficients: vec![1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0],
                ..CorrectionModel::default()
            }),
            ..Self::default()
        }
    }

    pub(crate) fn without_wb(mut self) -> Self {
        self.wb_diag = None;
        self
    }

    pub(crate) fn correction(&self) -> Option<&CorrectionModel> {
        self.correction.as_ref()
    }

    pub(crate) fn reference_context(&self) -> Option<&ResolvedReferenceContext> {
        self.reference_context.as_ref()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn non_srgb_stages_fail_closed_but_cc_only_is_supported() {
        for name in ccp_core::color::supported_rgb_color_spaces() {
            for white in ccp_core::color::supported_white_points() {
                let settings = correction_settings(name, white, "srgb", "d65").unwrap();
                assert!(validate_stage_input(&settings, false, false, false).is_ok());
                let expected = name == &"srgb" && white == &"d65";
                for flags in [
                    (true, false, false),
                    (false, true, false),
                    (false, false, true),
                ] {
                    assert_eq!(
                        validate_stage_input(&settings, flags.0, flags.1, flags.2).is_ok(),
                        expected
                    );
                }
            }
        }
    }

    #[test]
    fn saved_pipeline_roundtrips_nn_pls_linear() {
        for kind in [
            ModelKind::NeuralNet,
            ModelKind::Pls,
            ModelKind::Linear,
            ModelKind::Ccm3x3,
            ModelKind::RootPolynomial2,
        ] {
            let mut model = CorrectionModel {
                kind,
                feature_count: 8,
                coefficients: vec![0.1; 24],
                x_mean: vec![0.0; 8],
                x_std: vec![1.0; 8],
                ..CorrectionModel::default()
            };
            if kind == ModelKind::NeuralNet {
                model.hidden_size = 8;
                model.nn_residual = true;
                model.w1 = vec![0.01; 64];
                model.b1 = vec![0.0; 8];
                model.w2 = vec![0.02; 24];
                model.b2 = vec![0.0; 3];
                model.layer_weights = vec![model.w1.clone(), model.w2.clone()];
                model.layer_biases = vec![model.b1.clone(), model.b2.clone()];
            }
            if kind == ModelKind::Pls {
                model.pls_components = 4;
            }
            let saved = SavedPipeline {
                kind: kind.name().into(),
                correction: Some(model),
                do_cc: true,
                ffc_plane: Some([10, 20, 400, 300]),
                ffc_plane_source: Some("detected".into()),
                ffc_white_size: Some([800, 600]),
                ffc_params: Some(FfcFitParams {
                    method: FfcFitMethod::Poly,
                    degree: 5,
                    ..FfcFitParams::default()
                }),
                ..SavedPipeline::default()
            };
            let json = serde_json::to_string_pretty(&saved).unwrap();
            assert!(
                json.contains(&format!("\"kind\": \"{}\"", kind.name()))
                    || json.contains(&kind.name().to_string())
            );
            let back: SavedPipeline = serde_json::from_str(&json).unwrap();
            assert_eq!(back.kind, kind.name());
            let c = back.correction.expect("correction");
            assert_eq!(c.kind, kind);
            assert_eq!(c.coefficients.len(), 24);
            if kind == ModelKind::NeuralNet {
                assert_eq!(c.w1.len(), 64);
                assert_eq!(c.w2.len(), 24);
            }
            assert_eq!(back.ffc_plane, Some([10, 20, 400, 300]));
            assert_eq!(back.ffc_plane_source.as_deref(), Some("detected"));
            assert_eq!(
                back.ffc_params.as_ref().map(|p| p.method),
                Some(FfcFitMethod::Poly)
            );
        }
    }

    #[test]
    fn load_saved_rejects_pre_studio_models_with_refit_message() {
        let dir = std::env::temp_dir().join(format!("ccp-pre-studio-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let bare = CorrectionModel {
            kind: ModelKind::Ccm3x3,
            feature_count: 3,
            coefficients: vec![1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0],
            ..CorrectionModel::default()
        };
        let polynomial_gc = SavedPipeline {
            version: 1,
            do_gc: true,
            gc_coeffs: Some(vec![1.05, -2.0]),
            gc_model: None,
            ..SavedPipeline::default()
        };
        let mut uncurved_gc = SavedPipeline::all_stages_fixture();
        uncurved_gc.gc_model = Some(GammaModel {
            coeffs: vec![1.05, -2.0],
            knots: Vec::new(),
            values: Vec::new(),
            slopes: Vec::new(),
        });
        let cases = [
            serde_json::to_string(&bare).unwrap(),
            serde_json::to_string(&polynomial_gc).unwrap(),
            serde_json::to_string(&uncurved_gc).unwrap(),
        ];
        for (index, text) in cases.iter().enumerate() {
            let path = dir.join(format!("legacy-{index}.json"));
            let bare = index == 0;
            std::fs::write(&path, text).unwrap();
            let error = SavedPipeline::load(&path).expect_err("pre-Studio model must be rejected");
            assert_eq!(error.code(), ErrorCode::InvalidModel, "{error}");
            let message = error.to_string();
            assert!(
                message.contains("older than ColorCorrect Studio 0.1")
                    && message.contains("Fit a new calibration")
                    && (!bare || message.contains("Not a ColorCorrect saved pipeline")),
                "{message}"
            );
        }
        let current = SavedPipeline {
            gc_coeffs: Some(Vec::new()),
            ..SavedPipeline::all_stages_fixture()
        };
        let path = dir.join("current.json");
        std::fs::write(&path, serde_json::to_string(&current).unwrap()).unwrap();
        SavedPipeline::load(&path).unwrap();
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn load_saved_rejects_crafted_models_instead_of_panicking_on_apply() {
        let dir = std::env::temp_dir().join(format!("ccp-crafted-model-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let mut truncated = SavedPipeline::all_stages_fixture();
        if let Some(model) = truncated.correction.as_mut() {
            model.coefficients.truncate(2);
        }
        let mut miscounted = SavedPipeline::all_stages_fixture();
        miscounted.correction.as_mut().unwrap().feature_count = 1_000;
        let cases = [
            serde_json::to_string(&truncated).unwrap(),
            serde_json::to_string(&miscounted).unwrap(),
        ];
        for (index, text) in cases.iter().enumerate() {
            let path = dir.join(format!("crafted-{index}.json"));
            std::fs::write(&path, text).unwrap();
            let error = SavedPipeline::load(&path).expect_err("crafted model must be rejected");
            assert!(
                error.to_string().contains("invalid correction model"),
                "{error}"
            );
        }
        let valid = dir.join("valid.json");
        std::fs::write(
            &valid,
            serde_json::to_string(&SavedPipeline::all_stages_fixture()).unwrap(),
        )
        .unwrap();
        SavedPipeline::load(&valid).unwrap();
        let _ = std::fs::remove_dir_all(dir);
    }
}
