//! Calibration (`run`): fit FFC → saturation → GC → WB → CC on a chart image,
//! then write the corrected image, saved model, report and optional LUT.
//!
//! Stages mutate one f32 working buffer. After each stage the chart patches
//! are resampled from that buffer, so every fit sees the previous stages.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Instant;

use ccp_core::ffc::{apply_ffc_gc_fused_inplace, apply_ffc_inplace, FfcFitParams};
use ccp_core::honest::cross_validate_correction;
use ccp_core::lut::DEFAULT_LUT_SIZE;
use ccp_core::maps::{
    fit_correction_model, parse_model_kind, CorrectionSettings, MappingOptions, ModelKind,
    RIDGE_LAMBDA,
};
use ccp_core::phase2::emit_rpcc2_teacher;
use ccp_core::refs::{embedded_neutrals_table, load_neutrals_table, ChartPatch};
use ccp_core::sample::SamplingOptions;
use ccp_core::stages::{apply_gc_inplace, apply_wb_inplace, extrapolate_saturated, fit_gc, fit_wb};
use ndarray::Array3;
use serde::Serialize;
use serde_json::{json, Value};

use crate::charts::{self, Charts, Sampled};
use crate::error::{Error, Result};
use crate::ffc::{self, FfcGainCache, WhiteField};
use crate::model::{validate_stage_input, SavedPipeline, SpaceIds};
use crate::output::{self, PreviewMode};
use crate::render;
use crate::report::{self, build_report, patch_stats, stage_json};
use crate::request::{self, json_bool, json_str, IMAGE_KEYS};
use crate::work;

/// The `run` reply fields; `saved` stays with the worker for `export_cube`.
#[derive(Debug, Serialize)]
pub(crate) struct RunOutcome {
    output: PathBuf,
    report: PathBuf,
    model_out: PathBuf,
    lut_out: Option<PathBuf>,
    preview_dir: Option<PathBuf>,
    chart_type: String,
    has_delta_e: bool,
    report_json: Value,
    #[serde(skip)]
    saved: SavedPipeline,
}

impl RunOutcome {
    pub(crate) fn into_saved(self) -> SavedPipeline {
        self.saved
    }
}

struct FfcRequest {
    white: PathBuf,
    crop_rect: Option<String>,
    params: FfcFitParams,
}

/// A validated `run` request. Parsing rejects every invalid field before the
/// image is read or any artifact is written.
struct FitRequest {
    image: PathBuf,
    calibration_inference: Option<Value>,
    charts: Charts,
    do_sat: bool,
    do_gc: bool,
    do_wb: bool,
    do_cc: bool,
    ffc: Option<FfcRequest>,
    kind: ModelKind,
    spaces: SpaceIds,
    settings: CorrectionSettings,
    mapping: MappingOptions,
    sampling: SamplingOptions,
    random_sampling_requested: bool,
    output: PathBuf,
    report: PathBuf,
    model_out: PathBuf,
    preview_dir: Option<PathBuf>,
    lut_out: Option<PathBuf>,
    write_output: bool,
    preview_mode: PreviewMode,
    fast_mode: bool,
    skip_saturated: bool,
    cross_validate: bool,
    phase2_teacher: bool,
}

impl FitRequest {
    fn parse(req: &Value) -> Result<Self> {
        let image = PathBuf::from(
            json_str(req, &IMAGE_KEYS)
                .ok_or_else(|| Error::Message("run requires image".into()))?,
        );
        let handshake = charts::load_handshake(req)?;
        let charts = Charts::from_request(req, &handshake)?;
        let white = json_str(req, &["white"]);
        let do_ffc = json_bool(req, &["do_ffc"], white.is_some());
        if do_ffc && white.is_none() {
            return Err(Error::Message(ffc::MISSING_WHITE.into()));
        }
        let do_gc = json_bool(req, &["do_gc"], true);
        let do_wb = json_bool(req, &["do_wb"], true);
        let crop_rect = json_str(req, &["crop_rect"]).map(str::to_string);
        let ffc = match white.filter(|_| do_ffc) {
            Some(white) => Some(FfcRequest {
                white: PathBuf::from(white),
                crop_rect,
                params: request::ffc_params(req, request::ffc_method(req)?)?,
            }),
            None => None,
        };
        let model_name = match req.get("model").or_else(|| req.get("method")) {
            None => "rpcc2",
            Some(Value::String(name)) if !name.is_empty() => name,
            _ => {
                return Err(Error::Message(
                    "CC model must be a nonempty method name".into(),
                ))
            }
        };
        let kind = parse_model_kind(model_name)?;
        if let Some(basis) = req.get("basis") {
            if basis.as_str() != Some("polynomial")
                || !matches!(
                    kind,
                    ModelKind::Linear | ModelKind::Pls | ModelKind::NeuralNet
                )
            {
                return Err(Error::Message("only polynomial basis is supported for linear/PLS/NN; use the rpcc2/rpcc3 presets for root polynomials".into()));
            }
        }
        let spaces = SpaceIds::from_req(req);
        let settings = spaces.settings()?;
        validate_stage_input(&settings, do_ffc, do_gc, do_wb)?;
        let mapping = request::mapping(req);
        let sampling = request::sampling(req)?;
        let output = PathBuf::from(
            json_str(req, &["output", "output_path"])
                .ok_or_else(|| Error::Message("run requires output".into()))?,
        );
        let write_output = json_bool(req, &["write_output"], true);
        if write_output {
            output::check_still_path(&output)?;
        }
        Ok(Self {
            image,
            calibration_inference: handshake.get("inference").cloned(),
            charts,
            do_sat: json_bool(req, &["do_sat"], true),
            do_gc,
            do_wb,
            do_cc: json_bool(req, &["do_cc"], true),
            ffc,
            kind,
            spaces,
            settings,
            mapping,
            sampling,
            random_sampling_requested: req.get("samples_per_patch").is_some(),
            output,
            report: PathBuf::from(json_str(req, &["report"]).unwrap_or("report.json")),
            model_out: PathBuf::from(json_str(req, &["model_out"]).unwrap_or("model.json")),
            preview_dir: json_str(req, &["preview_dir"]).map(PathBuf::from),
            lut_out: json_str(req, &["lut_out"]).map(PathBuf::from),
            write_output,
            preview_mode: PreviewMode::from_req(req),
            fast_mode: json_bool(req, &["fast_mode"], false),
            skip_saturated: json_bool(req, &["skip_saturated"], false),
            cross_validate: json_bool(req, &["cross_validate"], true),
            phase2_teacher: json_bool(req, &["phase2_teacher"], true),
        })
    }

    /// The encoding of the corrected buffer: the target once CC ran.
    fn output_space(&self) -> &ccp_core::color::RgbColorSpace {
        if self.do_cc {
            &self.settings.target_space
        } else {
            &self.settings.input_space
        }
    }
}

/// Neutral patch names per chart: the installed table, else the embedded one.
fn load_neutrals() -> HashMap<String, Vec<String>> {
    if let Some(root) = ccp_detect::find_ccp_new_ui_root() {
        let path = root.join("refs").join("neutrals.json");
        if let Ok(table) = load_neutrals_table(Some(&path)) {
            return table;
        }
    }
    load_neutrals_table(None).unwrap_or_else(|_| embedded_neutrals_table())
}

/// Saturation is a property of the original capture, even if FFC/GC/WB later
/// lower its values. Patch ordering is immutable throughout one run.
fn restore_original_saturation(original: &[ChartPatch], current: &mut [ChartPatch]) {
    for (source, patch) in original.iter().zip(current.iter_mut()) {
        patch.saturated = source.saturated;
    }
}

pub(crate) fn run(
    req: &Value,
    progress: &mut dyn FnMut(&str),
    ffc_cache: &FfcGainCache,
) -> Result<RunOutcome> {
    render::reset_execution_record();
    let fit = FitRequest::parse(req)?;
    let neutrals = load_neutrals();
    let input_space = &fit.settings.input_space;
    let references = fit.charts.references();
    let resample = |work: &Array3<f32>| fit.charts.resample(work, fit.sampling, None);

    progress("Loading image");
    let loaded = work::load(&fit.image, input_space)?;
    let Sampled {
        spec,
        patches: before_patches,
        chart_type,
    } = fit.charts.sample(loaded.view(), fit.sampling)?;
    let mut work = work::from_image(loaded)?;
    let has_chart = !before_patches.is_empty();

    let mut stages_de = Vec::new();
    let mut prev_de = if has_chart {
        patch_stats(&before_patches, true, input_space)
    } else {
        report::UNMEASURED
    };

    let mut white_field: Option<(Arc<WhiteField>, f64)> = None;
    if let Some(ffc) = &fit.ffc {
        progress("FFC");
        let t = Instant::now();
        let fitted = ffc_cache
            .get_or_fit(&ffc.white, ffc.crop_rect.as_deref(), &ffc.params)
            .map_err(|error| ffc::failure(&error, format!("FFC could not complete: {error}")))?;
        white_field = Some((fitted, t.elapsed().as_secs_f64()));
    }
    let gain = white_field.as_ref().map(|(white, _)| white.gain());
    // With GC enabled the FFC gain is applied in GC's fused pass; until then
    // patches are sampled through the gain.
    let defer_fused_ffc_gc =
        gain.is_some() && fit.do_gc && !fit.preview_mode.wants_intermediate("ffc");
    let mut patches = if defer_fused_ffc_gc {
        fit.charts.resample(&work, fit.sampling, gain)?
    } else {
        if let Some(gain) = gain {
            apply_ffc_inplace(&mut work, gain);
            if fit.preview_mode.wants_intermediate("ffc") {
                output::stage_preview(fit.preview_dir.as_deref(), "ffc", &work)?;
            }
        }
        resample(&work)?
    };
    restore_original_saturation(&before_patches, &mut patches);
    if let Some((_, elapsed)) = white_field.as_ref().filter(|_| has_chart) {
        let now = patch_stats(&patches, false, input_space);
        stages_de.push(stage_json("FFC", prev_de, now, *elapsed));
        prev_de = now;
    }
    if fit.do_sat {
        progress("Saturation");
        let _ = extrapolate_saturated(
            &mut patches,
            fit.mapping.saturation_threshold,
            fit.mapping.clip_lo,
            fit.mapping.clip_hi,
        );
        restore_original_saturation(&before_patches, &mut patches);
    }

    // Fast mode renders the final image from the FFC-corrected source through
    // the combined LUT, after every stage has been fitted.
    let fast_source = fit.fast_mode.then(|| {
        let mut source = work.clone();
        if let Some(gain) = gain.filter(|_| defer_fused_ffc_gc) {
            apply_ffc_inplace(&mut source, gain);
        }
        source
    });
    // A deferred FFC is complete only once its fused GC pass has run.
    if gain.is_some() && (!defer_fused_ffc_gc || fit.fast_mode) {
        progress("FFC complete");
    }

    let mut saved = SavedPipeline::fitted(
        fit.kind,
        (fit.do_gc, fit.do_wb, fit.do_cc),
        &fit.spaces,
        white_field
            .as_ref()
            .zip(fit.ffc.as_ref())
            .map(|((white, _), ffc)| (white.as_ref(), ffc.params.clone())),
        references.cloned(),
        json!({"calibration": fit.calibration_inference,
            "plane": ccp_detect::plane::last_plane_inference()}),
    );

    if fit.do_gc {
        progress("GC");
        let t = Instant::now();
        let gc = fit_gc(&spec, &patches, Some(&neutrals))?;
        match gain.filter(|_| defer_fused_ffc_gc) {
            Some(gain) => {
                apply_ffc_gc_fused_inplace(&mut work, gain, &gc);
                if !fit.fast_mode {
                    progress("FFC complete");
                }
            }
            None => apply_gc_inplace(&mut work, &gc)?,
        }
        saved.record_gc(gc);
        patches = resample(&work)?;
        restore_original_saturation(&before_patches, &mut patches);
        let elapsed = t.elapsed().as_secs_f64();
        if fit.preview_mode.wants_intermediate("gc") {
            output::stage_preview(fit.preview_dir.as_deref(), "gc", &work)?;
        }
        if has_chart {
            let now = patch_stats(&patches, false, input_space);
            stages_de.push(stage_json("GC", prev_de, now, elapsed));
            prev_de = now;
        }
        progress("GC complete");
    }

    if fit.do_wb {
        progress("WB");
        let t = Instant::now();
        let wb = fit_wb(&spec, &patches, Some(&neutrals))?;
        apply_wb_inplace(&mut work, &wb);
        saved.record_wb(&wb);
        patches = resample(&work)?;
        restore_original_saturation(&before_patches, &mut patches);
        let elapsed = t.elapsed().as_secs_f64();
        if fit.preview_mode.wants_intermediate("wb") {
            output::stage_preview(fit.preview_dir.as_deref(), "wb", &work)?;
        }
        if has_chart {
            let now = patch_stats(&patches, false, input_space);
            stages_de.push(stage_json("WB", prev_de, now, elapsed));
            prev_de = now;
        }
        progress("WB complete");
    }

    if fit.skip_saturated {
        patches.retain(|p| !p.saturated);
    }

    let mut validation = None;
    let mut validation_elapsed = 0.0;
    let mut fit_warnings = Vec::new();
    if fit.do_cc && has_chart && fit.cross_validate {
        progress("Validating CC");
        let t = Instant::now();
        match cross_validate_correction(
            &patches,
            fit.kind,
            &fit.settings,
            RIDGE_LAMBDA,
            &fit.mapping,
        ) {
            Ok(report) => validation = Some(report),
            Err(error) => fit_warnings.push(format!("Cross-validation unavailable: {error}")),
        }
        validation_elapsed = t.elapsed().as_secs_f64();
    }
    if fit.kind == ModelKind::NeuralNet && fit.do_cc {
        fit_warnings.extend(nn_capacity_warnings(&fit.mapping, patches.len()));
    }

    let mut feature_count = 0i32;
    let mut cc_elapsed = 0.0;
    if fit.do_cc {
        progress("CC");
        let t = Instant::now();
        let model = fit_correction_model(
            &patches,
            fit.kind,
            &fit.settings,
            RIDGE_LAMBDA,
            &fit.mapping,
        )?;
        feature_count = model.feature_count;
        if !fit.fast_mode {
            render::apply_cc(&mut work, &model, &fit.settings)?;
        }
        saved.record_correction(model);
        cc_elapsed = t.elapsed().as_secs_f64();
        progress("CC complete");
    }

    let render_start = Instant::now();
    if let Some(source) = fast_source {
        work = source;
        let stages = saved.stages()?;
        let combined = render::combined_label(&stages);
        if let Some(label) = &combined {
            progress(label);
        }
        render::apply_combined(&mut work, &stages)?;
        if combined.is_some() {
            progress("Combined LUT complete");
        }
    }
    let combined_render_secs = if fit.fast_mode {
        render_start.elapsed().as_secs_f64()
    } else {
        0.0
    };
    cc_elapsed += combined_render_secs;

    progress("Writing outputs");
    output::finish_image_writes(
        &work,
        &fit.output,
        fit.preview_dir.as_deref(),
        fit.write_output,
        &fit.preview_mode,
        fit.output_space(),
    )?;
    saved.save(&fit.model_out)?;
    if fit.phase2_teacher {
        if let Ok(teacher) = emit_rpcc2_teacher(&patches, Some(&fit.settings)) {
            let teacher_path = fit.model_out.with_file_name("teacher_rpcc2.json");
            let mut teacher = serde_json::to_value(&teacher)?;
            if let Some(context) = references {
                teacher["reference_context"] = serde_json::to_value(context)?;
            }
            let _ = std::fs::write(teacher_path, serde_json::to_string_pretty(&teacher)?);
        }
    }
    if let Some(lut_path) = &fit.lut_out {
        saved.export_cube(lut_path, DEFAULT_LUT_SIZE)?;
    }

    let after_patches = if has_chart {
        resample(&work)?
    } else {
        Vec::new()
    };
    let mut report_json = build_report(
        fit.kind.name(),
        feature_count,
        &before_patches,
        &after_patches,
        has_chart,
        input_space,
        fit.output_space(),
    );
    report_json["apply_execution"] = json!(render::last_execution());
    saved.describe_in_report(&mut report_json);
    report_json["fast_mode"] = json!(fit.fast_mode);
    report_json["combined_render_secs"] = json!(combined_render_secs);
    report_json["fit_patch_count"] = json!(patches.len());
    if let Some(context) = references {
        report_json["reference_context"] = serde_json::to_value(context)?;
    }
    report_json["patch_sampling"] = if let Some((count, seed)) = fit.sampling.random {
        json!({"pattern":"random-lcg32-v1","samples_per_patch":count,
            "sampling_seed":seed,"aggregation":"mean"})
    } else {
        json!({"pattern":"grid-v1","samples_per_axis":fit.sampling.samples_per_axis,
            "samples_per_patch":fit.sampling.samples_per_axis * fit.sampling.samples_per_axis,
            "aggregation":"mean"})
    };
    report_json["input_normalization"] = json!({
        "exif_orientation": "applied before detect, sample and export",
        "embedded_icc": if work::is_srgb_d65(input_space) { "convert to working sRGB when present" } else { "explicit pixel encoding; ICC conversion bypassed" },
    });
    if has_chart && fit.do_cc {
        let now = patch_stats(&after_patches, false, &fit.settings.target_space);
        stages_de.push(stage_json("CC", prev_de, now, cc_elapsed));
    }
    if has_chart && !stages_de.is_empty() {
        report_json["stages"] = Value::Array(stages_de);
    }
    if let Some(validation) = validation.as_ref() {
        report_json["validation_label"] = json!("CC-only");
        report_json["validation_delta_e_2000"] = serde_json::to_value(validation)?;
        report_json["validation_scope"] = json!(
            "CC-only validation; GC/WB are fitted before folds. Not independent capture validation."
        );
        report_json["validation_elapsed_secs"] = json!(validation_elapsed);
        let training_mean = report_json["delta_e_2000"]["mean_after"]
            .as_f64()
            .unwrap_or(f64::NAN);
        if training_mean.is_finite() && validation.mean > training_mean * 2.0 {
            fit_warnings.push(format!(
                "Held-out ΔE {:.2} is much higher than training ΔE {:.2}; this fit is overfitting the chart.",
                validation.mean, training_mean
            ));
        } else if validation.mean > 10.0 {
            fit_warnings.push(format!(
                "High held-out mean ΔE {:.2}; verify the chart reference, patch order, exposure and color interpretation before reusing this model.", validation.mean));
        }
    }
    for stage in report_json["stages"].as_array().into_iter().flatten() {
        let before = stage["mean_before"].as_f64().unwrap_or(0.0);
        let after = stage["mean_after"].as_f64().unwrap_or(0.0);
        if after > before + 0.25 {
            fit_warnings.push(format!(
                "{} increased chart mean ΔE from {:.2} to {:.2}; compare this optional stage disabled. These scores depend on the selected chart reference.",
                stage["name"].as_str().unwrap_or("Stage"), before, after));
        }
    }
    if !fit_warnings.is_empty() {
        report_json["fit_warnings"] = json!(fit_warnings);
    }
    report_json["saturation_extrapolation_enabled"] = json!(fit.do_sat);
    if !fit.random_sampling_requested {
        report_json["fit_samples_per_axis"] = json!(fit.sampling.samples_per_axis);
    }
    if let Some(parent) = fit.report.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(&fit.report, serde_json::to_string_pretty(&report_json)?)?;
    progress("Writing outputs complete");

    Ok(RunOutcome {
        has_delta_e: has_chart && report_json.get("metrics_hidden") == Some(&Value::Bool(false)),
        output: fit.output,
        report: fit.report,
        model_out: fit.model_out,
        lut_out: fit.lut_out,
        preview_dir: fit.preview_dir,
        report_json,
        saved,
        chart_type,
    })
}

/// High-variance NN settings for a chart-sized training set.
fn nn_capacity_warnings(mapping: &MappingOptions, patches: usize) -> Vec<String> {
    let mut warnings = Vec::new();
    if patches < 64 && mapping.nn_batch_norm {
        warnings.push(format!(
            "Batch normalization is high-variance with only {patches} chart patches; trust held-out ΔE, not the training fit."
        ));
    }
    if patches < 64 && mapping.nn_dropout > 0.0 {
        warnings.push(format!(
            "Dropout {:.0}% is usually unstable with only {patches} chart patches; compare held-out ΔE.",
            mapping.nn_dropout * 100.0
        ));
    }
    if mapping.hidden_layers.len() > 2 {
        warnings.push(format!(
            "{} hidden layers are high-capacity for {patches} chart patches; a 64 or 100-unit single layer is the safer baseline.",
            mapping.hidden_layers.len()
        ));
    }
    warnings
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use std::path::Path;

    use ccp_core::reference_profile::ReferenceProfile;
    use ccp_core::refs::{chart_spec, ChartKind};
    use image::RgbImage as Rgb8;

    use crate::apply::apply_saved_to_image;

    pub(crate) fn run_from_json(req: &Value, progress: &mut dyn FnMut(&str)) -> Result<RunOutcome> {
        run(req, progress, &FfcGainCache::default())
    }

    pub(crate) fn classic_handshake(width: u32, height: u32) -> Value {
        let cols = 6u32;
        let rows = 4u32;
        let cw = width as f64 / cols as f64;
        let ch = height as f64 / rows as f64;
        let mut obbs = Vec::new();
        for r in 0..rows {
            for c in 0..cols {
                let x0 = c as f64 * cw;
                let y0 = r as f64 * ch;
                let x1 = x0 + cw;
                let y1 = y0 + ch;
                obbs.push(json!([[x0, y0], [x1, y0], [x1, y1], [x0, y1]]));
            }
        }
        json!({
            "ok": true,
            "chart_type": "classic_4x6",
            "cli_chart": "classic",
            "patch_obbs": obbs,
        })
    }

    pub(crate) fn write_cast_classic(path: &Path, w: u32, h: u32) {
        let spec = chart_spec(ChartKind::Classic).unwrap();
        let cols = 6u32;
        let rows = 4u32;
        let cw = w / cols;
        let ch = h / rows;
        let mut img = Rgb8::new(w, h);
        for r in 0..rows {
            for c in 0..cols {
                let i = (r * cols + c) as usize;
                let rgb = spec.reference_srgb[i];
                let pix = image::Rgb([
                    (rgb[0] * 1.18 * 255.0).clamp(0.0, 255.0) as u8,
                    (rgb[1] * 0.88 * 255.0).clamp(0.0, 255.0) as u8,
                    (rgb[2] * 0.80 * 255.0).clamp(0.0, 255.0) as u8,
                ]);
                for y in (r * ch)..((r + 1) * ch) {
                    for x in (c * cw)..((c + 1) * cw) {
                        img.put_pixel(x, y, pix);
                    }
                }
            }
        }
        img.save(path).unwrap();
    }

    pub(crate) fn preview_exists(dir: &Path, stem: &str) -> bool {
        dir.join(format!("{stem}.rgb")).is_file() || dir.join(format!("{stem}.png")).is_file()
    }

    impl RunOutcome {
        pub(crate) fn saved(&self) -> &SavedPipeline {
            &self.saved
        }

        pub(crate) fn output(&self) -> &Path {
            &self.output
        }

        pub(crate) fn model_out(&self) -> &Path {
            &self.model_out
        }

        pub(crate) fn report_json(&self) -> &Value {
            &self.report_json
        }
    }

    #[test]
    fn fit_progress_finishes_real_fused_stages_and_artifacts_before_announcing_done() {
        render::set_lut_request(true, false);
        let dir = std::env::temp_dir().join(format!("ccp-progress-fit-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        let white = dir.join("white.png");
        write_cast_classic(&image, 120, 80);
        Rgb8::from_pixel(120, 80, image::Rgb([180; 3]))
            .save(&white)
            .unwrap();
        for fast in [false, true] {
            let output = dir.join(format!("output-{fast}.png"));
            let report = dir.join(format!("report-{fast}.json"));
            let model = dir.join(format!("model-{fast}.json"));
            let request = json!({"image":image,"white":white,"crop_rect":"0,0,120,80",
                "handshake":classic_handshake(120,80),"output":output,"report":report,"model_out":model,
                "do_ffc":true,"do_gc":true,"do_wb":true,"do_cc":true,"model":"ccm",
                "cross_validate":false,"ffc":{"method":"linear"},"fast_mode":fast});
            let mut events = Vec::new();
            run_from_json(&request, &mut |message| {
                if message == "Writing outputs complete" {
                    assert!(output.is_file() && model.is_file() && report.is_file());
                }
                events.push(message.to_owned());
            })
            .unwrap();
            for stage in ["FFC", "GC", "WB", "CC"] {
                let start = events.iter().position(|value| value == stage).unwrap();
                let done = events
                    .iter()
                    .position(|value| value == &format!("{stage} complete"))
                    .unwrap();
                assert!(done > start);
            }
            assert_eq!(events.last().unwrap(), "Writing outputs complete");
            assert_eq!(
                events
                    .iter()
                    .filter(|value| value.starts_with("Combined LUT ·"))
                    .count(),
                usize::from(fast)
            );
            if !fast {
                assert!(
                    events
                        .iter()
                        .position(|value| value == "FFC complete")
                        .unwrap()
                        > events.iter().position(|value| value == "GC").unwrap(),
                    "fused FFC cannot finish before its actual GC pass"
                );
            }
        }
        render::set_lut_request(false, false);
    }

    #[test]
    fn ffc_request_without_white_fails_before_loading_the_image() {
        let request = json!({"image": "missing.jpg", "handshake": classic_handshake(120, 80),
            "output": "unused.png", "do_ffc": true});
        let error = run_from_json(&request, &mut |_| {})
            .err()
            .unwrap()
            .to_string();
        assert!(
            error.contains("FFC requires a blank white-field image"),
            "{error}"
        );
    }

    fn custom_profile(id: &str, offset: f64) -> ReferenceProfile {
        let spec = chart_spec(ChartKind::Classic).unwrap();
        ReferenceProfile {
            version: 1,
            id: id.into(),
            name: id.into(),
            revision: 1,
            chart: ChartKind::Classic,
            color_space: "lab".into(),
            white_point: "d50".into(),
            scale: 1.0,
            order: "row-major".into(),
            values: spec
                .reference_lab_d50
                .iter()
                .map(|lab| [lab.l + offset, lab.a, lab.b])
                .collect(),
        }
    }

    #[test]
    fn custom_profiles_survive_all_stages_selection_model_report_and_teacher() {
        let dir = std::env::temp_dir().join(format!(
            "ccp-custom-reference-pipeline-{}",
            std::process::id()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let mut a = classic_handshake(120, 80);
        a["chart_id"] = json!("s:a");
        let mut b = a.clone();
        b["chart_id"] = json!("s:b");
        let handshake = json!({"snapshot_id":"s", "charts":[a,b]});
        let profiles = vec![
            custom_profile("card-a", 0.0),
            custom_profile("card-b", -3.0),
        ];
        let assignments = json!([{"chart_id":"s:a","profile_id":"card-a","profile_revision":1}, {"chart_id":"s:b","profile_id":"card-b","profile_revision":1}]);
        for (suffix, use_all, face) in [("both", true, 0), ("selected", false, 1)] {
            let run_dir = dir.join(suffix);
            std::fs::create_dir_all(&run_dir).unwrap();
            let request = json!({"image":image,"handshake":handshake,"use_all":use_all,"face":face,
                "reference_profiles":profiles,"reference_assignments":assignments,"output":run_dir.join("corrected.png"),
                "report":run_dir.join("report.json"),"model_out":run_dir.join("model.json"),
                "model":"ccm","do_gc":true,"do_wb":true,"do_cc":true,"cross_validate":false,"skip_saturated":true});
            let outcome = run_from_json(&request, &mut |_| {}).unwrap();
            let context = outcome.saved().reference_context().unwrap();
            assert_eq!(context.charts.len(), if use_all { 2 } else { 1 });
            assert_eq!(context.charts.last().unwrap().chart_id, "s:b");
            let report = &outcome.report_json;
            let count = if use_all { 48 } else { 24 };
            assert_eq!(report["patches"].as_array().unwrap().len(), count);
            let last_chart = &report["patches"][count - 24];
            assert_eq!(last_chart["provenance"]["chart_id"], "s:b");
            assert!(
                (last_chart["reference_lab_d50"]["l"].as_f64().unwrap() - profiles[1].values[0][0])
                    .abs()
                    < 1e-8
            );
            let saturated = report["saturated_patches"].as_u64().unwrap();
            assert!(saturated > 0);
            assert_eq!(
                report["fit_patch_count"].as_u64().unwrap(),
                count as u64 - saturated
            );
            let reloaded = SavedPipeline::load(&outcome.model_out).unwrap();
            let reloaded_context = reloaded.reference_context().unwrap();
            assert_eq!(
                reloaded_context.reference_profiles,
                context.reference_profiles
            );
            assert_eq!(
                reloaded_context.reference_assignments,
                context.reference_assignments
            );
            for (a, b) in reloaded_context.charts.iter().zip(&context.charts) {
                assert_eq!(a.chart_id, b.chart_id);
                for (la, lb) in a
                    .spec
                    .reference_lab_d50
                    .iter()
                    .zip(&b.spec.reference_lab_d50)
                {
                    assert!(
                        (la.l - lb.l).abs() < 1e-12
                            && (la.a - lb.a).abs() < 1e-12
                            && (la.b - lb.b).abs() < 1e-12
                    );
                }
            }
            let teacher: Value =
                serde_json::from_slice(&std::fs::read(run_dir.join("teacher_rpcc2.json")).unwrap())
                    .unwrap();
            let persisted_report: Value =
                serde_json::from_slice(&std::fs::read(&outcome.report).unwrap()).unwrap();
            assert_eq!(
                teacher["reference_context"],
                persisted_report["reference_context"]
            );
            let apply_out = run_dir.join("applied.png");
            apply_saved_to_image(&reloaded, &image, &apply_out).unwrap();
            assert!(apply_out.is_file());
        }
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn custom_assignment_errors_precede_image_loading_and_outputs() {
        let mut hs = classic_handshake(120, 80);
        hs["snapshot_id"] = json!("s");
        hs["chart_id"] = json!("s:a");
        let req = json!({"image":"never-read.png","handshake":hs,"reference_profiles":[custom_profile("card",0.0)],
            "reference_assignments":[], "output":"never-write.png"});
        let error = run_from_json(&req, &mut |_| {}).unwrap_err().to_string();
        assert!(error.contains("assignment"), "{error}");
    }

    #[test]
    fn effective_sampling_reports_and_saved_replay_preserve_both_patterns() {
        let dir =
            std::env::temp_dir().join(format!("ccp-sampling-integration-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let mut baseline = None;
        for (index, count) in [None, None, Some(1u32), Some(10), Some(20), Some(50)]
            .into_iter()
            .enumerate()
        {
            let mut request = json!({"image":image,"handshake":classic_handshake(120,80),
                "output":dir.join(format!("fit-{index}.png")),
                "report":dir.join(format!("report-{index}.json")),
                "model_out":dir.join(format!("model-{index}.json")),
                "model":"ccm","do_ffc":false,"do_sat":false,"cross_validate":false,
                "phase2_teacher":false,"preview_mode":"none"});
            if let Some(n) = count {
                request["samples_per_patch"] = json!(n);
                request["sampling_seed"] = json!(42);
            } else if index == 1 {
                request["fit_samples"] = json!(9);
            }
            let fitted = run_from_json(&request, &mut |_| {}).unwrap();
            let info = &fitted.report_json["patch_sampling"];
            assert_eq!(info["aggregation"], "mean");
            if let Some(n) = count {
                assert_eq!(info["pattern"], "random-lcg32-v1");
                assert_eq!(info["samples_per_patch"], n);
                assert_eq!(info["sampling_seed"], 42);
                assert!(fitted.report_json.get("fit_samples_per_axis").is_none());
            } else {
                assert_eq!(info["pattern"], "grid-v1");
                assert_eq!(info["samples_per_axis"], 9);
                assert_eq!(info["samples_per_patch"], 81);
                assert_eq!(fitted.report_json["fit_samples_per_axis"], 9);
                let value = serde_json::to_value(fitted.saved()).unwrap();
                if let Some(previous) = baseline.as_ref() {
                    assert_eq!(&value, previous);
                } else {
                    baseline = Some(value);
                }
            }
            let restored = SavedPipeline::load(&fitted.model_out).unwrap();
            let applied = dir.join(format!("applied-{index}.png"));
            apply_saved_to_image(&restored, &image, &applied).unwrap();
            assert_eq!(
                std::fs::read(&fitted.output).unwrap(),
                std::fs::read(applied).unwrap()
            );
        }
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn invalid_export_and_method_selectors_do_not_create_fit_artifacts() {
        let dir =
            std::env::temp_dir().join(format!("ccp-request-rejection-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let request = json!({"image":dir.join("missing.png"),"handshake":classic_handshake(120,80),
            "output":dir.join("output.png"),"report":dir.join("report.json"),
            "model_out":dir.join("model.json"),"do_ffc":false});
        for (field, value) in [
            ("model", json!("unsupported")),
            ("model", json!(true)),
            ("model", Value::Null),
            ("basis", json!("hybrid")),
            ("output", json!(dir.join("bad.bmp"))),
            ("fit_samples", json!(1.5)),
            ("fit_samples", json!(4_294_967_305u64)),
        ] {
            let mut invalid = request.clone();
            invalid[field] = value;
            let message = run_from_json(&invalid, &mut |_| {})
                .err()
                .unwrap()
                .to_string();
            assert!(
                !message.contains("os error 2"),
                "validation must precede image loading: {message}"
            );
            assert!(
                !dir.join("output.png").exists()
                    && !dir.join("report.json").exists()
                    && !dir.join("model.json").exists()
                    && !dir.join("bad.bmp").exists()
            );
        }
        let bad = dir.join("new-directory/output.bmp");
        let srgb = ccp_core::color::srgb_d65_space().1;
        assert!(output::finish_image_writes(
            &Array3::zeros((1, 1, 3)),
            &bad,
            None,
            true,
            &PreviewMode::None,
            srgb
        )
        .is_err());
        assert!(!bad.parent().unwrap().exists());
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn nn_request_options_train_and_roundtrip_for_every_optimizer() {
        let dir =
            std::env::temp_dir().join(format!("ccp-bridge-nn-options-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        for optimizer in ["adam", "sgd", "rmsprop"] {
            let model_path = dir.join(format!("{optimizer}.json"));
            let req = json!({
                "image": image.to_string_lossy(),
                "handshake": classic_handshake(120, 80),
                "output": dir.join(format!("{optimizer}.png")).to_string_lossy(),
                "report": dir.join(format!("{optimizer}-report.json")).to_string_lossy(),
                "model_out": model_path.to_string_lossy(),
                "preview_mode": "none",
                "write_output": true,
                "do_ffc": false,
                "do_gc": false,
                "do_wb": false,
                "model": "nn",
                "hidden_layers": [16, 8],
                "nn_optimizer": optimizer,
                "nn_batch_norm": true,
                "nn_dropout": 0.2,
                "nn_lr": 0.005,
                "nn_epochs": 80,
                "nn_patience": 20,
            });
            let outcome = run_from_json(&req, &mut |_| {}).unwrap();
            assert!(outcome.output.is_file());
            let saved = SavedPipeline::load(&model_path).unwrap();
            let model = saved.correction().expect("NN correction model");
            assert_eq!(model.hidden_layers, vec![16, 8]);
            assert_eq!(model.nn_optimizer, optimizer);
            assert!(model.nn_batch_norm);
            assert!(model.nn_residual);
            assert!((model.nn_dropout - 0.2).abs() < 1e-12);
            assert_eq!(model.layer_weights.len(), 3);
            let corrected = model.apply_linear([0.4, 0.3, 0.2]).unwrap();
            assert!(corrected.iter().all(|value| value.is_finite()));
            let report: Value = serde_json::from_slice(
                &std::fs::read(dir.join(format!("{optimizer}-report.json"))).unwrap(),
            )
            .unwrap();
            let before = report["delta_e_2000"]["mean_before"].as_f64().unwrap();
            let after = report["delta_e_2000"]["mean_after"].as_f64().unwrap();
            assert!(
                after <= before,
                "{optimizer} regressed Delta E {before} -> {after}"
            );
        }
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn run_fit_writes_outputs_and_honest_de() {
        let dir = std::env::temp_dir().join(format!("ccp-bridge-run-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let handshake = classic_handshake(120, 80);
        let req = json!({
            "image": image.to_string_lossy(),
            "handshake": handshake,
            "output": dir.join("out.png").to_string_lossy(),
            "report": dir.join("report.json").to_string_lossy(),
            "model_out": dir.join("model.json").to_string_lossy(),
            "preview_dir": dir.join("prev").to_string_lossy(),
            "lut_out": dir.join("shot.cube").to_string_lossy(),
            "model": "rpcc2",
            "do_ffc": false,
            "do_sat": true,
            "do_gc": true,
            "do_wb": true,
            "do_cc": true,
            "fit_samples": 5,
        });
        let mut notes = Vec::new();
        for (field, val) in [
            ("basis", json!("hybrid")),
            ("random_seed", json!(42)),
            ("n_samples", json!(10)),
        ] {
            let mut invalid = req.clone();
            invalid[field] = val;
            assert!(run_from_json(&invalid, &mut |_| {}).is_err());
        }
        let out = run_from_json(&req, &mut |m| notes.push(m.to_string())).unwrap();
        assert_eq!(out.report_json["fit_samples_per_axis"], 5);
        assert_eq!(out.report_json["saturation_extrapolation_enabled"], true);
        assert!(out.output.is_file());
        assert!(out.report.is_file());
        assert!(out.model_out.is_file());
        assert!(dir.join("shot.cube").is_file());
        assert!(
            preview_exists(&dir.join("prev"), "corrected"),
            "missing corrected preview"
        );
        for name in ["original", "gc", "wb", "cc"] {
            assert!(
                !preview_exists(&dir.join("prev"), name),
                "lazy apply must not write {name} preview"
            );
        }
        assert!(
            !preview_exists(&dir.join("prev"), "ffc"),
            "FFC preview must be skipped when FFC did not run"
        );
        assert_eq!(out.report_json["do_ffc"], false);
        assert_eq!(out.report_json["do_gc"], true);
        assert_eq!(out.report_json["do_wb"], true);
        assert_eq!(out.report_json["do_cc"], true);
        assert_eq!(out.report_json["ffc_method"], "off");
        assert!(
            out.report_json["process"]
                .as_str()
                .unwrap_or("")
                .contains("FFC off · GC safe · WB · CC"),
            "report must record the UI step config: {}",
            out.report_json["process"]
        );
        assert!(out.has_delta_e, "chart fit must expose honest ΔE");
        let de = &out.report_json["delta_e_2000"];
        assert!(de["mean_before"].as_f64().unwrap().is_finite());
        assert!(de["mean_after"].as_f64().unwrap().is_finite());
        let stages = out.report_json["stages"]
            .as_array()
            .expect("chart fit must report per-stage ΔE from existing handshake samples");
        let names: Vec<&str> = stages
            .iter()
            .map(|s| s["name"].as_str().unwrap_or(""))
            .collect();
        assert_eq!(
            names,
            vec!["GC", "WB", "CC"],
            "FFC off must omit FFC: {names:?}"
        );
        for stage in stages {
            assert!(stage["mean_before"].as_f64().unwrap().is_finite());
            assert!(stage["mean_after"].as_f64().unwrap().is_finite());
        }
        let last_after = stages.last().unwrap()["mean_after"].as_f64().unwrap();
        let overall_after = de["mean_after"].as_f64().unwrap();
        assert!(
            (last_after - overall_after).abs() < 1e-9,
            "CC after must match overall mean_after: {last_after} vs {overall_after}"
        );
        let cube = std::fs::read_to_string(dir.join("shot.cube")).unwrap();
        assert!(cube.contains("LUT does not include flat-field"));
        assert!(!notes.is_empty());
    }
}
