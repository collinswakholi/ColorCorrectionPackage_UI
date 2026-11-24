//! Applying a saved model to new images (`apply` and `preview_stage`).
//!
//! No detection and no invented chart metrics. FFC is refitted from the
//! current white image when enabled; it is never part of a saved model.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Instant;

use ccp_core::ffc::{apply_ffc_gc_fused_inplace, apply_ffc_inplace, FfcFitMethod, FfcFitParams};
use ccp_core::lut::LutPipeline;
use ccp_core::stages::{apply_gc_inplace, apply_wb_inplace};
use ndarray::{Array2, Array3};
use serde::Serialize;
use serde_json::Value;

use crate::error::{Error, Result};
use crate::ffc::{self, FfcGainCache};
use crate::model::{validate_stage_input, SavedPipeline};
use crate::output::{self, PreviewMode};
use crate::render;
use crate::request::{self, json_bool, json_str};
use crate::work;

/// How one `apply` or `preview_stage` request renders a saved model.
#[derive(Clone, Debug)]
pub(crate) struct ApplyOptions {
    /// Optional combined 65³ GC/WB/CC LUT. Default preserves separate stages.
    fast_mode: bool,
    do_ffc: bool,
    do_gc: bool,
    do_wb: bool,
    do_cc: bool,
    white: Option<PathBuf>,
    crop_rect: Option<String>,
    ffc: FfcFitParams,
    write_output: bool,
    preview_mode: PreviewMode,
    ffc_cache: Arc<FfcGainCache>,
}

/// Wall-clock phases of one applied image, as reported in `timings`.
#[derive(Debug, Serialize)]
pub(crate) struct ApplyTimings {
    decode_seconds: f64,
    processing_seconds: f64,
    export_seconds: f64,
}

impl ApplyOptions {
    /// Stage toggles default to the saved model; `do_ffc` defaults off. FFC
    /// parameters are validated only when FFC is enabled.
    pub(crate) fn from_request(
        req: &Value,
        saved: &SavedPipeline,
        ffc_cache: Arc<FfcGainCache>,
    ) -> Result<Self> {
        let do_ffc = json_bool(req, &["do_ffc"], false);
        let ffc_from_req = |method| match req.get("ffc") {
            Some(_) => request::ffc_params(req, method),
            None => Ok(saved.ffc_params().cloned().unwrap_or_default()),
        };
        let ffc = if do_ffc {
            let ffc = ffc_from_req(request::ffc_method(req)?)?;
            ffc.validate()?;
            ffc
        } else {
            ffc_from_req(FfcFitMethod::Poly).unwrap_or_default()
        };
        Ok(Self {
            fast_mode: json_bool(req, &["fast_mode"], false),
            do_ffc,
            do_gc: json_bool(req, &["do_gc"], saved.gc_enabled()),
            do_wb: json_bool(req, &["do_wb"], saved.wb_enabled()),
            do_cc: json_bool(req, &["do_cc"], saved.cc_enabled()),
            white: json_str(req, &["white"]).map(PathBuf::from),
            crop_rect: json_str(req, &["crop_rect"]).map(str::to_string),
            ffc,
            write_output: json_bool(req, &["write_output"], true),
            preview_mode: PreviewMode::from_req(req),
            ffc_cache,
        })
    }

    /// Render only the cumulative `stage` (`ffc`, `gc`, `wb`, `cc` or
    /// `corrected`) as a preview, without the full-resolution output.
    pub(crate) fn for_stage_preview(mut self, stage: &str) -> Result<Self> {
        self.write_output = false;
        self.preview_mode = PreviewMode::Stage(stage.to_string());
        match stage {
            // Intermediate previews expose the original stage math. Fast mode
            // only approximates a final rendered image, not a stage.
            "ffc" => {
                (self.fast_mode, self.do_gc, self.do_wb, self.do_cc) = (false, false, false, false)
            }
            "gc" => (self.fast_mode, self.do_wb, self.do_cc) = (false, false, false),
            "wb" => (self.fast_mode, self.do_cc) = (false, false),
            "cc" | "corrected" => {}
            other => return Err(Error::Message(format!("unknown stage: {other}"))),
        }
        Ok(self)
    }

    /// The saved model's stages this request enables.
    fn stages(&self, saved: &SavedPipeline) -> Result<LutPipeline> {
        let mut stages = saved.stages()?;
        if !self.do_gc {
            stages.gc = None;
        }
        if !self.do_wb {
            stages.wb = None;
        }
        if !self.do_cc {
            stages.cc = None;
        }
        Ok(stages)
    }

    /// Apply FFC (with `gain`) and `stages` to `work`. The callback marks actual
    /// stage boundaries: fast mode reports its transforms as one combined
    /// operation rather than inventing sequential work.
    fn render(
        &self,
        work: &mut Array3<f32>,
        stages: &LutPipeline,
        gain: Option<&Array2<f32>>,
        preview_dir: Option<&Path>,
        progress: &mut dyn FnMut(&str),
    ) -> Result<()> {
        let wants = |stage: &str| self.preview_mode.wants_intermediate(stage);
        let preview = |stage: &str, work: &Array3<f32>, progress: &mut dyn FnMut(&str)| {
            if preview_dir.is_some() && wants(stage) {
                progress("Writing preview");
                output::stage_preview(preview_dir, stage, work)?;
                progress("Writing preview complete");
            }
            Ok::<_, Error>(())
        };
        if self.fast_mode {
            if let Some(gain) = gain {
                apply_ffc_inplace(work, gain);
                progress("FFC complete");
                preview("ffc", work, progress)?;
            }
            if wants("gc") || wants("wb") {
                let mut intermediate = work.clone();
                if let Some(gc) = &stages.gc {
                    apply_gc_inplace(&mut intermediate, gc)?;
                    preview("gc", &intermediate, progress)?;
                }
                if let Some(wb) = &stages.wb {
                    apply_wb_inplace(&mut intermediate, wb);
                    preview("wb", &intermediate, progress)?;
                }
            }
            let combined = render::combined_label(stages);
            if let Some(label) = &combined {
                progress(label);
            }
            render::apply_combined(work, stages)?;
            if combined.is_some() {
                progress("Combined LUT complete");
            }
            return Ok(());
        }
        match (gain, &stages.gc) {
            (Some(gain), Some(gc)) if !wants("ffc") => {
                progress("GC");
                apply_ffc_gc_fused_inplace(work, gain, gc);
                progress("FFC complete");
                progress("GC complete");
                preview("gc", work, progress)?;
            }
            _ => {
                if let Some(gain) = gain {
                    apply_ffc_inplace(work, gain);
                    progress("FFC complete");
                    preview("ffc", work, progress)?;
                }
                if let Some(gc) = &stages.gc {
                    progress("GC");
                    apply_gc_inplace(work, gc)?;
                    progress("GC complete");
                    preview("gc", work, progress)?;
                }
            }
        }
        if let Some(wb) = &stages.wb {
            progress("WB");
            apply_wb_inplace(work, wb);
            progress("WB complete");
            preview("wb", work, progress)?;
        }
        if let Some((model, settings)) = &stages.cc {
            progress("CC");
            render::apply_cc(work, model, settings)?;
            progress("CC complete");
        }
        Ok(())
    }
}

/// Apply `saved` to `image`, writing `output` and the requested previews.
pub(crate) fn apply_to_image(
    saved: &SavedPipeline,
    image: &Path,
    output: &Path,
    preview_dir: Option<&Path>,
    apply: &ApplyOptions,
    progress: &mut dyn FnMut(&str),
) -> Result<ApplyTimings> {
    render::reset_execution_record();
    if apply.write_output {
        output::check_still_path(output)?;
    }
    let settings = saved.settings()?;
    validate_stage_input(&settings, apply.do_ffc, apply.do_gc, apply.do_wb)?;
    progress("Loading image");
    let started = Instant::now();
    let mut work = work::from_image(work::load(image, &settings.input_space)?)?;
    let decoded = Instant::now();
    let white_field = if apply.do_ffc {
        let white = apply
            .white
            .as_ref()
            .ok_or_else(|| Error::Message(ffc::MISSING_WHITE.into()))?;
        progress("FFC");
        let fitted = apply
            .ffc_cache
            .get_or_fit(white, apply.crop_rect.as_deref(), &apply.ffc)
            .map_err(|e| ffc::failure(&e, format!("FFC reference could not be loaded or fitted: {e}. Provide a readable blank white-field image from the same capture setup.")))?;
        Some(fitted)
    } else {
        None
    };
    let stages = apply.stages(saved)?;
    apply.render(
        &mut work,
        &stages,
        white_field.as_ref().map(|w| w.gain()),
        preview_dir,
        progress,
    )?;
    let write_message = if apply.write_output {
        Some("Writing outputs")
    } else if preview_dir.is_some() && apply.preview_mode.wants_corrected() {
        Some("Writing preview")
    } else {
        None
    };
    if let Some(message) = write_message {
        progress(message);
    }
    let processed = Instant::now();
    output::finish_image_writes(
        &work,
        output,
        preview_dir,
        apply.write_output,
        &apply.preview_mode,
        if stages.cc.is_some() {
            &settings.target_space
        } else {
            &settings.input_space
        },
    )?;
    if let Some(message) = write_message {
        progress(&format!("{message} complete"));
    }
    Ok(ApplyTimings {
        decode_seconds: decoded.duration_since(started).as_secs_f64(),
        processing_seconds: processed.duration_since(decoded).as_secs_f64(),
        export_seconds: processed.elapsed().as_secs_f64(),
    })
}

#[cfg(test)]
impl ApplyOptions {
    /// The saved model's own stages, without FFC, writing the output only.
    pub(crate) fn from_saved(saved: &SavedPipeline) -> Self {
        Self::from_request(&serde_json::json!({}), saved, Arc::default()).unwrap()
    }
}

/// Apply `saved` with its own stages and no previews.
#[cfg(test)]
pub(crate) fn apply_saved_to_image(
    saved: &SavedPipeline,
    image: &Path,
    output: &Path,
) -> Result<()> {
    apply_to_image(
        saved,
        image,
        output,
        None,
        &ApplyOptions::from_saved(saved),
        &mut |_| {},
    )?;
    Ok(())
}

/// [`apply_to_image`]'s processing of an in-memory image with a given gain.
#[cfg(test)]
fn apply_to_work(
    work: &mut Array3<f32>,
    saved: &SavedPipeline,
    apply: &ApplyOptions,
    gain: Option<&Array2<f32>>,
) -> Result<()> {
    if apply.do_ffc && gain.is_none() {
        return Err(Error::Message(
            "FFC requested but no white-field gain map was supplied".into(),
        ));
    }
    render::reset_execution_record();
    let stages = apply.stages(saved)?;
    apply.render(
        work,
        &stages,
        gain.filter(|_| apply.do_ffc),
        None,
        &mut |_| {},
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use ccp_core::maps::CorrectionSettings;
    use ccp_core::stages::apply_encoded_lut_inplace;
    use image::RgbImage as Rgb8;
    use serde_json::json;

    use crate::test_support::{
        classic_handshake, preview_exists, run_from_json, write_cast_classic,
    };

    #[test]
    fn apply_progress_groups_fast_transforms_and_omits_disabled_or_absent_work() {
        render::set_lut_request(true, false);
        let dir = std::env::temp_dir().join(format!("ccp-progress-fast-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("image.png");
        let white = dir.join("white.png");
        Rgb8::from_pixel(48, 32, image::Rgb([110, 120, 130]))
            .save(&image)
            .unwrap();
        Rgb8::from_pixel(48, 32, image::Rgb([180; 3]))
            .save(&white)
            .unwrap();
        let saved = SavedPipeline::all_stages_fixture();
        let mut options = ApplyOptions::from_saved(&saved);
        options.fast_mode = true;
        options.write_output = false;
        options.do_ffc = true;
        options.white = Some(white);
        options.crop_rect = Some("0,0,48,32".into());
        options.ffc.method = FfcFitMethod::Linear;
        let output = dir.join("must-not-write.png");
        let mut events = Vec::new();
        apply_to_image(&saved, &image, &output, None, &options, &mut |message| {
            events.push(message.to_owned())
        })
        .unwrap();
        assert_eq!(
            events,
            [
                "Loading image",
                "FFC",
                "FFC complete",
                "Combined LUT · GC + WB + CC",
                "Combined LUT complete"
            ]
        );
        assert!(!output.exists());

        options.do_ffc = false;
        options.do_gc = false;
        options.do_cc = false;
        events.clear();
        apply_to_image(&saved, &image, &output, None, &options, &mut |message| {
            events.push(message.to_owned())
        })
        .unwrap();
        assert_eq!(
            events,
            [
                "Loading image",
                "Combined LUT · WB",
                "Combined LUT complete"
            ]
        );
        let absent = saved.clone().without_wb();
        events.clear();
        apply_to_image(&absent, &image, &output, None, &options, &mut |message| {
            events.push(message.to_owned())
        })
        .unwrap();
        assert_eq!(
            events,
            ["Loading image"],
            "identity rendering must not claim absent stages or writes"
        );
        render::set_lut_request(false, false);
    }

    #[test]
    fn apply_progress_marks_only_successful_ffc_and_writes_complete() {
        render::set_lut_request(true, false);
        let dir = std::env::temp_dir().join(format!("ccp-progress-errors-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("image.png");
        Rgb8::from_pixel(48, 32, image::Rgb([110; 3]))
            .save(&image)
            .unwrap();
        let saved = SavedPipeline::all_stages_fixture();
        let mut options = ApplyOptions::from_saved(&saved);
        options.fast_mode = true;
        options.do_ffc = true;
        options.white = Some(dir.join("missing-white.png"));
        // A valid extension reaches the stage and write failures exercised below.
        let output = dir.join("existing-output-directory.png");
        std::fs::create_dir_all(&output).unwrap();
        let retained = output.join("previous-result.txt");
        std::fs::write(&retained, b"retain previous artifact").unwrap();
        let mut events = Vec::new();
        assert!(
            apply_to_image(&saved, &image, &output, None, &options, &mut |message| {
                events.push(message.to_owned())
            })
            .is_err()
        );
        assert_eq!(events, ["Loading image", "FFC"]);
        options.do_ffc = false;
        events.clear();
        assert!(
            apply_to_image(&saved, &image, &output, None, &options, &mut |message| {
                events.push(message.to_owned())
            })
            .is_err()
        );
        assert_eq!(events.last().unwrap(), "Writing outputs");
        assert!(!events
            .iter()
            .any(|value| value == "Writing outputs complete"));
        assert_eq!(
            std::fs::read(&retained).unwrap(),
            b"retain previous artifact"
        );
        let successful = dir.join("successful.png");
        events.clear();
        apply_to_image(
            &saved,
            &image,
            &successful,
            None,
            &options,
            &mut |message| {
                if message == "Writing outputs complete" {
                    assert_eq!(
                        image::open(&successful).unwrap().to_rgb8().dimensions(),
                        (48, 32)
                    );
                }
                events.push(message.to_owned());
            },
        )
        .unwrap();
        assert_eq!(events.last().unwrap(), "Writing outputs complete");
        let legacy = dir.join("legacy.png");
        apply_to_image(&saved, &image, &legacy, None, &options, &mut |_| {}).unwrap();
        assert_eq!(
            std::fs::read(successful).unwrap(),
            std::fs::read(legacy).unwrap()
        );
        render::set_lut_request(false, false);
    }

    #[test]
    fn apply_progress_lazy_preview_finishes_after_usable_rgb_and_does_not_write_output() {
        render::set_lut_request(true, false);
        let dir = std::env::temp_dir().join(format!("ccp-progress-preview-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("image.png");
        Rgb8::from_pixel(48, 32, image::Rgb([110; 3]))
            .save(&image)
            .unwrap();
        let output = dir.join("must-not-write.png");
        let previews = dir.join("previews");
        let saved = SavedPipeline::all_stages_fixture();
        let mut options = ApplyOptions::from_saved(&saved);
        options.do_wb = false;
        options.do_cc = false;
        options.write_output = false;
        options.preview_mode = PreviewMode::Stage("gc".into());
        let mut events = Vec::new();
        apply_to_image(
            &saved,
            &image,
            &output,
            Some(&previews),
            &options,
            &mut |message| {
                if message == "Writing preview complete" {
                    assert!(
                        previews.join("gc.rgb").is_file(),
                        "done requires the usable RGB sidecar, not asynchronous PNG completion"
                    );
                }
                events.push(message.to_owned());
            },
        )
        .unwrap();
        assert_eq!(
            events,
            [
                "Loading image",
                "GC",
                "GC complete",
                "Writing preview",
                "Writing preview complete"
            ]
        );
        assert!(!output.exists());
        output::flush_preview_encodes().unwrap();
        render::set_lut_request(false, false);
    }

    #[test]
    fn fast_rendering_keeps_fused_ffc_fit_coefficients() {
        render::set_lut_request(true, false);
        let dir = std::env::temp_dir().join(format!("ccp-fast-ffc-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        let white = dir.join("white.png");
        write_cast_classic(&image, 120, 80);
        Rgb8::from_fn(120, 80, |x, y| image::Rgb([180 + ((x + y) / 5) as u8; 3]))
            .save(&white)
            .unwrap();
        let mut request = json!({"image":image,"white":white,"crop_rect":"0,0,120,80",
            "handshake":classic_handshake(120,80),"output":dir.join("corrected.png"),
            "report":dir.join("report.json"),"model_out":dir.join("model.json"),
            "do_ffc":true,"do_gc":true,"do_wb":true,"do_cc":true,"model":"rpcc2",
            "cross_validate":false,"ffc":{"method":"linear"},"preview_mode":"corrected"});
        let normal = run_from_json(&request, &mut |_| {}).unwrap();
        request["fast_mode"] = json!(true);
        let fast = run_from_json(&request, &mut |_| {}).unwrap();
        assert_eq!(
            serde_json::to_value(normal.saved()).unwrap(),
            serde_json::to_value(fast.saved()).unwrap()
        );
        let mut options = ApplyOptions::from_saved(fast.saved());
        options.fast_mode = true;
        options.do_ffc = true;
        options.white = Some(white);
        options.crop_rect = Some("0,0,120,80".into());
        options.ffc = fast.saved().ffc_params().cloned().unwrap();
        let reload = dir.join("reload.png");
        apply_to_image(fast.saved(), &image, &reload, None, &options, &mut |_| {}).unwrap();
        assert_eq!(
            image::open(fast.output()).unwrap().to_rgb8(),
            image::open(reload).unwrap().to_rgb8()
        );
        render::set_lut_request(false, false);
    }

    #[test]
    fn fast_calibration_preserves_fits_previews_and_reload_for_all_model_families() {
        render::set_lut_request(true, false);
        let dir = std::env::temp_dir().join(format!("ccp-fast-models-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        for kind in ["ccm", "rpcc2", "rpcc3", "linear", "pls", "nn"] {
            let mut saved_json = None;
            for fast in [false, true] {
                let output = dir.join(format!("{kind}-{fast}.png"));
                let previews = dir.join(format!("{kind}-{fast}-previews"));
                let request = json!({"image":image,"handshake":classic_handshake(120,80),
                    "output":output,"report":dir.join("report.json"),"model_out":dir.join("model.json"),
                    "model":kind,"do_ffc":false,"do_gc":true,"do_wb":true,"do_cc":true,
                    "nn_epochs":40,"hidden_size":8,"cross_validate":false,"fast_mode":fast,
                    "preview_dir":previews,"preview_mode":"all"});
                let fitted = run_from_json(&request, &mut |_| {}).unwrap();
                let current = serde_json::to_value(fitted.saved()).unwrap();
                if let Some(normal) = &saved_json {
                    assert_eq!(&current, normal, "{kind} fit changed");
                } else {
                    saved_json = Some(current);
                }
                assert_eq!(fitted.report_json()["fast_mode"], fast);
                if fast {
                    assert_eq!(
                        fitted.report_json()["apply_execution"]["method"],
                        "combined-lut65"
                    );
                    let loaded = SavedPipeline::load(fitted.model_out()).unwrap();
                    let mut options = ApplyOptions::from_saved(&loaded);
                    options.fast_mode = true;
                    let reapplied = dir.join(format!("{kind}-reloaded.png"));
                    apply_to_image(&loaded, &image, &reapplied, None, &options, &mut |_| {})
                        .unwrap();
                    assert_eq!(
                        image::open(&output).unwrap().to_rgb8(),
                        image::open(&reapplied).unwrap().to_rgb8()
                    );
                    output::flush_preview_encodes().unwrap();
                    for stage in ["gc", "wb"] {
                        let before = dir
                            .join(format!("{kind}-false-previews"))
                            .join(format!("{stage}.png"));
                        let after = previews.join(format!("{stage}.png"));
                        assert_eq!(
                            image::open(before).unwrap().to_rgb8(),
                            image::open(after).unwrap().to_rgb8()
                        );
                        let lazy = dir.join(format!("{kind}-lazy-{stage}"));
                        let mut preview_options = options.clone();
                        preview_options.write_output = false;
                        preview_options.preview_mode = PreviewMode::Stage(stage.into());
                        preview_options.do_cc = false;
                        preview_options.do_wb = stage == "wb";
                        apply_to_image(
                            &loaded,
                            &image,
                            &reapplied,
                            Some(&lazy),
                            &preview_options,
                            &mut |_| {},
                        )
                        .unwrap();
                        output::flush_preview_encodes().unwrap();
                        assert_eq!(
                            image::open(previews.join(format!("{stage}.png")))
                                .unwrap()
                                .to_rgb8(),
                            image::open(lazy.join(format!("{stage}.png")))
                                .unwrap()
                                .to_rgb8(),
                            "{kind} lazy {stage} must preserve stage math"
                        );
                    }
                }
            }
        }
        render::set_lut_request(false, false);
    }

    #[test]
    fn ffc_rejects_unsupported_active_method_but_ignores_disabled_settings() {
        let saved = SavedPipeline::default();
        for bad in [json!("svm"), json!(5)] {
            let mut req = json!({"do_ffc":true, "ffc":{"method":bad}});
            assert!(ApplyOptions::from_request(&req, &saved, Arc::default()).is_err());
            req["do_ffc"] = json!(false);
            assert!(ApplyOptions::from_request(&req, &saved, Arc::default()).is_ok());
        }
        assert_eq!(
            request::ffc_method(&json!({"ffc":{"method":"Polynomial"}})).unwrap(),
            FfcFitMethod::Poly
        );
        // Undocumented pre-0.2 spellings are no longer read.
        assert_eq!(
            request::ffc_method(&json!({"ffc_method":"nn","ffc":{"fit_method":"pls"}})).unwrap(),
            FfcFitMethod::Poly
        );
    }

    #[test]
    fn ffc_rejects_out_of_range_active_params_and_accepts_ui_ranges() {
        let saved = SavedPipeline::default();
        for (key, bad) in [
            ("bins", json!(1)),
            ("bins", json!(1_000_001)),
            ("degree", json!(0)),
            ("degree", json!(1_000)),
            ("smooth_window", json!(0)),
            ("smooth_window", json!(100_001)),
            ("nn_max_iter", json!(0)),
            ("nn_max_iter", json!(1_000_000_000_i64)),
        ] {
            let mut req = json!({"do_ffc":true, "ffc":{"method":"nn", key:bad}});
            let error = ApplyOptions::from_request(&req, &saved, Arc::default()).unwrap_err();
            assert!(error.to_string().contains(key), "{key}: {error}");
            req["do_ffc"] = json!(false);
            assert!(ApplyOptions::from_request(&req, &saved, Arc::default()).is_ok());
            let run = json!({"image":"missing.jpg", "output":"out.jpg", "white":"w.jpg",
                "do_ffc":true, "ffc":{"method":"nn", key:bad}, "handshake":classic_handshake(40, 30)});
            let error = run_from_json(&run, &mut |_| {}).unwrap_err();
            assert!(error.to_string().contains(key), "run {key}: {error}");
        }
        // Every value the desktop UI can send (bins 4-200, odd smooth 1-51,
        // degree 1-7, iterations 50-20000) and the SDK defaults stay valid.
        for (bins, smooth, degree, iters) in
            [(4, 1, 1, 50), (200, 51, 7, 20_000), (50, 5, 5, 10_000)]
        {
            let req = json!({"do_ffc":true, "ffc":{"method":"nn", "bins":bins,
                "smooth_window":smooth, "degree":degree, "nn_max_iter":iters}});
            let options = ApplyOptions::from_request(&req, &saved, Arc::default()).unwrap();
            assert_eq!(options.ffc.bins, bins);
            assert_eq!(options.ffc.degree, degree);
        }
        // Historical even-window rounding is preserved.
        let req = json!({"do_ffc":true, "ffc":{"smooth":4}});
        assert_eq!(
            ApplyOptions::from_request(&req, &saved, Arc::default())
                .unwrap()
                .ffc
                .smooth_window,
            5
        );
    }

    #[test]
    fn requested_frame_ffc_without_gain_fails_before_mutation() {
        let mut work = Array3::<f32>::from_elem((2, 2, 3), 0.42);
        let original = work.clone();
        let options = ApplyOptions {
            fast_mode: false,
            do_ffc: true,
            ..ApplyOptions::from_saved(&SavedPipeline::default())
        };
        let error =
            apply_to_work(&mut work, &SavedPipeline::default(), &options, None).unwrap_err();
        assert!(error.to_string().contains("no white-field gain"));
        assert_eq!(original, work);
    }

    #[test]
    fn requested_ffc_fit_and_apply_fail_closed_for_invalid_white() {
        let dir = std::env::temp_dir().join(format!("ccp-ffc-fail-closed-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let invalid = dir.join("invalid.png");
        std::fs::write(&invalid, b"invalid image").unwrap();
        for (i, white) in [dir.join("missing.png"), invalid, dir.clone()]
            .iter()
            .enumerate()
        {
            let output = dir.join(format!("output-{i}.png"));
            let req = json!({"image":image,"handshake":classic_handshake(120,80),"output":output,"white":white,"do_ffc":true});
            assert!(run_from_json(&req, &mut |_| {})
                .unwrap_err()
                .to_string()
                .contains("blank white-field image"));
            let options = ApplyOptions {
                do_ffc: true,
                white: Some(white.clone()),
                ..ApplyOptions::from_saved(&SavedPipeline::default())
            };
            assert!(apply_to_image(
                &SavedPipeline::default(),
                &image,
                &output,
                None,
                &options,
                &mut |_| {}
            )
            .unwrap_err()
            .to_string()
            .contains("blank white-field image"));
            assert!(!output.exists());
        }
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn disabled_ffc_ignores_unused_invalid_white_for_fit_and_apply() {
        let dir = std::env::temp_dir().join(format!("ccp-ffc-disabled-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let invalid = dir.join("invalid-white.png");
        std::fs::write(&invalid, b"not a readable image").unwrap();
        let request = json!({"image": image, "handshake": classic_handshake(120, 80),
            "output": dir.join("fitted.png"), "report": dir.join("report.json"),
            "model_out": dir.join("model.json"), "white": invalid,
            "do_ffc": false, "ffc":{"method":"unsupported"}, "phase2_teacher": false});
        let fitted = run_from_json(&request, &mut |_| {}).unwrap();
        assert_eq!(fitted.report_json()["do_ffc"], false);
        let output = dir.join("applied.png");
        let apply = ApplyOptions {
            do_ffc: false,
            white: Some(invalid),
            ..ApplyOptions::from_saved(&SavedPipeline::default())
        };
        apply_to_image(fitted.saved(), &image, &output, None, &apply, &mut |_| {}).unwrap();
        assert!(output.is_file());
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn apply_hides_invented_delta_e() {
        let dir = std::env::temp_dir().join(format!("ccp-bridge-apply-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let req = json!({
            "image": image.to_string_lossy(),
            "handshake": classic_handshake(120, 80),
            "output": dir.join("fit.png").to_string_lossy(),
            "report": dir.join("fit_report.json").to_string_lossy(),
            "model_out": dir.join("model.json").to_string_lossy(),
            "do_ffc": false,
            "model": "rpcc2",
        });
        let out = run_from_json(&req, &mut |_| {}).unwrap();
        let saved = SavedPipeline::load(out.model_out()).unwrap();
        let applied = dir.join("applied.png");
        apply_saved_to_image(&saved, &image, &applied).unwrap();
        assert!(applied.is_file());
        let settings = CorrectionSettings::srgb_d65().unwrap();
        let hidden = crate::report::build_report(
            "rpcc2",
            0,
            &[],
            &[],
            false,
            &settings.input_space,
            &settings.target_space,
        );
        assert_eq!(hidden["metrics_hidden"], true);
        assert!(hidden.get("delta_e_2000").is_none());
        assert!(
            hidden.get("stages").is_none(),
            "apply-only must not invent stage ΔE"
        );

        let prev = dir.join("apply_prev");
        std::fs::create_dir_all(&prev).unwrap();
        let gated = dir.join("gated.png");
        let apply = ApplyOptions {
            fast_mode: false,
            do_ffc: true,
            do_gc: false,
            do_wb: true,
            do_cc: true,
            white: Some(image.clone()),
            crop_rect: Some("0,0,120,80".into()),
            ffc: FfcFitParams {
                method: FfcFitMethod::Linear,
                ..FfcFitParams::default()
            },
            write_output: true,
            preview_mode: PreviewMode::All,
            ffc_cache: Arc::default(),
        };
        apply_to_image(&saved, &image, &gated, Some(&prev), &apply, &mut |_| {}).unwrap();
        assert!(gated.is_file());
        assert!(
            preview_exists(&prev, "ffc"),
            "apply-model must re-run FFC when enabled"
        );
        assert!(
            !preview_exists(&prev, "gc"),
            "apply-model must honor GC off"
        );
        assert!(preview_exists(&prev, "wb"));
        assert!(preview_exists(&prev, "corrected"));
    }

    #[test]
    fn spatial_ffc_remains_separate_and_noncontiguous_images_work() {
        render::set_lut_request(true, false);
        let saved = SavedPipeline::all_stages_fixture();
        let mut options = ApplyOptions::from_saved(&saved);
        options.fast_mode = true;
        options.do_ffc = true;
        let mut input = Array3::from_shape_fn((40, 32, 3), |(y, x, c)| {
            ((x * 7 + y * 31 + c) % 256) as f32 / 255.
        });
        input.swap_axes(0, 1);
        let gain = Array2::from_shape_fn((32, 40), |(y, x)| 0.85 + (x + y) as f32 / 180.);
        let mut expected = input.clone();
        apply_ffc_inplace(&mut expected, &gain);
        let table = render::combined_lut_table(&options.stages(&saved).unwrap());
        apply_encoded_lut_inplace(&mut expected, &table, 65);
        apply_to_work(&mut input, &saved, &options, Some(&gain)).unwrap();
        assert_eq!(input, expected);
        assert!(apply_to_work(&mut input, &saved, &options, None).is_err());
        render::set_lut_request(false, false);
    }

    #[test]
    fn extended_values_preserve_standard_semantics_and_nonfinite_input_is_atomic() {
        render::set_lut_request(true, false);
        let saved = SavedPipeline::all_stages_fixture();
        let mut options = ApplyOptions::from_saved(&saved);
        let source = Array3::from_shape_fn((32, 40, 3), |(y, x, c)| {
            ((x * 7 + y * 31 + c) % 300) as f32 / 255. - 0.05
        });
        let mut expected = source.clone();
        apply_to_work(&mut expected, &saved, &options, None).unwrap();
        options.fast_mode = true;
        let mut actual = source;
        apply_to_work(&mut actual, &saved, &options, None).unwrap();
        assert_eq!(actual, expected);
        actual[[0, 0, 0]] = f32::INFINITY;
        let before = actual.clone();
        assert!(apply_to_work(&mut actual, &saved, &options, None).is_err());
        assert_eq!(actual, before);
        render::set_lut_request(false, false);
    }

    #[test]
    fn public_identity_fixture_preserves_pixels_in_both_cpu_methods() {
        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../../testdata/acceptance/identity-model.json");
        let saved = SavedPipeline::load(path).expect("the public identity fixture must load");
        let fields = serde_json::to_value(&saved).unwrap();
        for flag in ["do_ffc", "do_gc", "do_wb", "do_cc"] {
            assert_eq!(fields[flag], false, "{flag}");
        }
        for stage in ["correction", "gc_coeffs", "gc_model", "wb_diag"] {
            assert!(fields.get(stage).is_none_or(Value::is_null), "{stage}");
        }
        let original = Array3::from_shape_vec(
            (2, 3, 3),
            vec![
                0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 0.03, 0.5, 0.9, 0.73, 0.11, 0.26, 1.0, 0.0, 0.25,
                0.18, 0.18, 0.18,
            ],
        )
        .unwrap();
        render::set_lut_request(true, false);
        for fast_mode in [false, true] {
            let mut image = original.clone();
            let options = ApplyOptions {
                fast_mode,
                ..ApplyOptions::from_saved(&saved)
            };
            apply_to_work(&mut image, &saved, &options, None).unwrap();
            for (&before, &after) in original.iter().zip(image.iter()) {
                assert!(after.is_finite() && (before - after).abs() <= 1e-6);
            }
            assert_eq!(render::last_backend(), "cpu");
        }
        render::set_lut_request(false, false);
    }
}
