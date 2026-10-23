//! Native CPU fit-only experiment. No detection, IO, GC/WB, validation folds,
//! image application, LUT construction, or Python IPC enters the timed region.
//!
//! fit_bench [ccm,rpcc2,linear,pls,nn] [repetitions=20] [blocks=5]
//!           [nn_epochs=200] [nn_patience=50] [warmups=3]
use std::{hint::black_box, time::Instant};

use ccp_core::{
    color::{delta_e_2000, encoded_to_linear_rgb, linear_rgb_to_xyz, xyz_to_lab},
    maps::{
        apply_correction_pixel, build_encoded_lut, fit_correction_model, parse_model_kind,
        sample_encoded_lut, CorrectionModel, CorrectionSettings, MappingOptions, ModelKind,
    },
    refs::{chart_spec, ChartKind, ChartPatch, Vec2},
    RIDGE_LAMBDA,
};
use serde_json::json;

fn synthetic_patches() -> Result<Vec<ChartPatch>, String> {
    let spec = chart_spec(ChartKind::Classic).map_err(|error| error.to_string())?;
    let mut patches = Vec::with_capacity(spec.patch_count());
    for (index, reference) in spec.reference_srgb.iter().enumerate() {
        let [r, g, b] = *reference;
        // A deterministic encoded-RGB cross-channel cast. This is a numerical
        // training fixture, not a camera response or physical color target.
        let measured = [
            0.88 * r + 0.04 * g + 0.02 * b + 0.010,
            0.93 * g + 0.015 * r + 0.03 * b + 0.008,
            0.84 * b + 0.06 * r + 0.02 * g + 0.015,
        ]
        .map(|value| value.clamp(0.0, 1.0));
        patches.push(ChartPatch {
            provenance: None,
            name: spec.names[index].clone(),
            index,
            reference_lab_d50: spec.reference_lab_d50[index],
            reference_srgb: *reference,
            measured_srgb: measured,
            original_measured_srgb: measured,
            center: Vec2 {
                x: (index % spec.columns) as f64,
                y: (index / spec.columns) as f64,
            },
            saturated: false,
        });
    }
    Ok(patches)
}

fn validate_model(
    model: &CorrectionModel,
    patches: &[ChartPatch],
    settings: &CorrectionSettings,
) -> Result<f64, String> {
    let finite = model
        .coefficients
        .iter()
        .chain(&model.w1)
        .chain(&model.b1)
        .chain(&model.w2)
        .chain(&model.b2)
        .chain(&model.x_mean)
        .chain(&model.x_std)
        .chain(&model.y_mean)
        .chain(&model.y_std)
        .chain(&model.intercept)
        .chain(model.layer_weights.iter().flatten())
        .chain(model.layer_biases.iter().flatten())
        .chain([
            &model.nn_best_validation_score,
            &model.nn_best_validation_loss,
        ])
        .all(|value| value.is_finite());
    if !finite || model.feature_count <= 0 {
        return Err("fit returned nonfinite coefficients/diagnostics or no features".into());
    }
    let mut training_error = 0.0;
    for patch in patches {
        let corrected = apply_correction_pixel(model, patch.measured_srgb, settings)
            .map_err(|error| error.to_string())?;
        if !corrected.iter().all(|value| value.is_finite()) {
            return Err("fit returned nonfinite training prediction".into());
        }
        training_error += corrected
            .iter()
            .zip(patch.reference_srgb)
            .map(|(a, b)| (a - b).abs())
            .sum::<f64>();
    }
    // Prediction health outside the fitted 24 points; no invented ground truth.
    for index in 0..64 {
        let input = [
            (index % 4) as f64 / 3.0,
            ((index / 4) % 4) as f64 / 3.0,
            (index / 16) as f64 / 3.0,
        ];
        if !apply_correction_pixel(model, input, settings)
            .map_err(|error| error.to_string())?
            .iter()
            .all(|value| value.is_finite())
        {
            return Err("fit returned nonfinite grid prediction".into());
        }
    }
    Ok(training_error / (patches.len() * 3) as f64)
}

fn positive(args: &[String], position: usize, default: usize) -> Result<usize, String> {
    let value = args
        .get(position)
        .map(|value| value.parse::<usize>())
        .transpose()
        .map_err(|error| error.to_string())?
        .unwrap_or(default);
    if value == 0 {
        return Err("counts and epoch budgets must be positive".into());
    }
    Ok(value)
}

fn error_statistics(mut errors: Vec<f64>) -> Result<serde_json::Value, String> {
    if errors.is_empty()
        || !errors
            .iter()
            .all(|error| error.is_finite() && *error >= 0.0)
    {
        return Err("approximation errors must be finite nonnegative values".into());
    }
    let mean = errors.iter().sum::<f64>() / errors.len() as f64;
    errors.sort_by(f64::total_cmp);
    let percentile_index = (errors.len() * 95).div_ceil(100) - 1;
    Ok(
        json!({"n":errors.len(), "max":errors[errors.len()-1], "mean":mean,
              "p95":errors[percentile_index], "percentile_method":"nearest rank"}),
    )
}

/// Model approximation is intentionally measured once after all timed fits.
/// Its truth is the model's direct evaluation, not a physical reference target.
fn lut_approximation(
    model: &CorrectionModel,
    settings: &CorrectionSettings,
) -> Result<serde_json::Value, String> {
    const GRID: usize = 32;
    let mut inputs = Vec::with_capacity(GRID.pow(3));
    let mut direct_outputs = Vec::with_capacity(GRID.pow(3));
    let mut direct_lab = Vec::with_capacity(GRID.pow(3));
    let to_lab = |rgb| {
        xyz_to_lab(
            linear_rgb_to_xyz(
                encoded_to_linear_rgb(rgb, &settings.target_space),
                &settings.target_space,
            ),
            &settings.target_white,
        )
    };
    for b in 0..GRID {
        for g in 0..GRID {
            for r in 0..GRID {
                // Odd multiples of 1/64 lie between all 17³ and 33³ LUT nodes.
                let input = [
                    (r as f64 + 0.5) / GRID as f64,
                    (g as f64 + 0.5) / GRID as f64,
                    (b as f64 + 0.5) / GRID as f64,
                ];
                let direct = apply_correction_pixel(model, input, settings)
                    .map_err(|error| error.to_string())?;
                if !direct.iter().all(|value| value.is_finite()) {
                    return Err("nonfinite direct model output in approximation grid".into());
                }
                let lab = to_lab(direct);
                if ![lab.l, lab.a, lab.b].iter().all(|value| value.is_finite()) {
                    return Err("nonfinite direct model Lab in approximation grid".into());
                }
                inputs.push(input);
                direct_outputs.push(direct);
                direct_lab.push(lab);
            }
        }
    }
    let mut results = Vec::new();
    for size in [17usize, 33] {
        let lut = build_encoded_lut(model, settings, size).map_err(|error| error.to_string())?;
        if lut.len() != size.pow(3) * 3 || !lut.iter().all(|value| value.is_finite()) {
            return Err("invalid encoded LUT in model approximation experiment".into());
        }
        let mut channel_errors = Vec::with_capacity(inputs.len() * 3);
        let mut pixel_max_errors = Vec::with_capacity(inputs.len());
        let mut delta_e = Vec::with_capacity(inputs.len());
        for ((input, direct), lab) in inputs.iter().zip(&direct_outputs).zip(&direct_lab) {
            let approximate = sample_encoded_lut(&lut, size, *input);
            if !approximate.iter().all(|value| value.is_finite()) {
                return Err("nonfinite LUT output in approximation grid".into());
            }
            let errors = [
                (direct[0] - approximate[0]).abs(),
                (direct[1] - approximate[1]).abs(),
                (direct[2] - approximate[2]).abs(),
            ];
            channel_errors.extend_from_slice(&errors);
            pixel_max_errors.push(errors.into_iter().fold(0.0f64, f64::max));
            let approximate_lab = to_lab(approximate);
            if ![approximate_lab.l, approximate_lab.a, approximate_lab.b]
                .iter()
                .all(|value| value.is_finite())
            {
                return Err("nonfinite LUT Lab in approximation grid".into());
            }
            delta_e.push(delta_e_2000(*lab, approximate_lab));
        }
        results.push(
            json!({"lut_grid_size":size, "lut_storage":"f32", "interpolation":"trilinear",
            "encoded_rgb_absolute_channel_error":error_statistics(channel_errors)?,
            "encoded_rgb_max_channel_error_per_pixel":error_statistics(pixel_max_errors)?,
            "delta_e2000_direct_model_vs_lut":error_statistics(delta_e)?}),
        );
    }
    Ok(json!({"sample_count":inputs.len(), "sample_grid_size":GRID,
        "sample_definition":"R=(r+0.5)/32,G=(g+0.5)/32,B=(b+0.5)/32 for r,g,b in 0..31; RGB encoded values in [0,1]",
        "sample_order":"B outermost, G middle, R innermost; channels RGB",
        "direct_evaluation":"apply_correction_pixel with the final fitted model and recorded sRGB/D65 settings",
        "lab_white":"d65", "timed_fit_region":false, "results":results,
        "limitations":["Off-grid synthetic cube points were not used as training pairs; they are not independent physical capture truth.",
            "Errors include f32 LUT node quantization plus trilinear approximation of the f64 direct model.",
            "These model approximation errors are separate from backend arithmetic parity on an identical LUT."]}))
}

fn run() -> Result<(), String> {
    let args: Vec<String> = std::env::args().collect();
    let models = args
        .get(1)
        .map(String::as_str)
        .unwrap_or("ccm,rpcc2,linear,pls,nn");
    let repetitions = positive(&args, 2, 20)?;
    let blocks = positive(&args, 3, 5)?;
    let epochs = i32::try_from(positive(&args, 4, 200)?).map_err(|error| error.to_string())?;
    let patience = i32::try_from(positive(&args, 5, 50)?).map_err(|error| error.to_string())?;
    let warmups = args
        .get(6)
        .map(|value| value.parse::<usize>())
        .transpose()
        .map_err(|error| error.to_string())?
        .unwrap_or(3);
    let settings = CorrectionSettings::srgb_d65().map_err(|error| error.to_string())?;
    let patches = synthetic_patches()?;
    let mapping = MappingOptions {
        nn_epochs: epochs,
        nn_patience: patience,
        ..MappingOptions::default()
    };
    for name in models.split(',') {
        let kind = parse_model_kind(name).map_err(|error| error.to_string())?;
        for _ in 0..warmups {
            let model = fit_correction_model(&patches, kind, &settings, RIDGE_LAMBDA, &mapping)
                .map_err(|error| error.to_string())?;
            black_box(validate_model(&model, &patches, &settings)?);
        }
        let mut runs = Vec::with_capacity(blocks.saturating_mul(repetitions));
        let mut final_model = None;
        for block in 0..blocks {
            for repetition in 0..repetitions {
                let start = Instant::now();
                let model = fit_correction_model(
                    black_box(&patches),
                    kind,
                    &settings,
                    RIDGE_LAMBDA,
                    &mapping,
                )
                .map_err(|error| error.to_string())?;
                black_box(&model);
                let elapsed = start.elapsed().as_secs_f64() * 1000.0;
                // Validation/evaluation/serialization are outside the fit timer.
                let training_rgb_mae = validate_model(&model, &patches, &settings)?;
                runs.push(json!({"block": block, "run": repetition, "ms": elapsed,
                    "epochs_trained": if kind == ModelKind::NeuralNet { Some(model.nn_epochs_trained) } else { None },
                    "early_stopped": if kind == ModelKind::NeuralNet { Some(model.nn_epochs_trained < epochs) } else { None },
                    "best_internal_score": if kind == ModelKind::NeuralNet { Some(model.nn_best_validation_score) } else { None },
                    "best_internal_loss": if kind == ModelKind::NeuralNet { Some(model.nn_best_validation_loss) } else { None },
                    "synthetic_training_rgb_mae": training_rgb_mae}));
                final_model = Some(model);
            }
        }
        let approximation = if matches!(
            kind,
            ModelKind::Ccm3x3 | ModelKind::RootPolynomial2 | ModelKind::NeuralNet
        ) {
            Some(lut_approximation(
                final_model.as_ref().ok_or("no fitted model")?,
                &settings,
            )?)
        } else {
            None
        };
        println!(
            "{}",
            json!({"status":"measured", "backend":"native-rust-cpu", "model":kind.name(),
            "nn_budget_class":if epochs == MappingOptions::default().nn_epochs && patience == MappingOptions::default().nn_patience {"production-default"} else {"explicit-experimental-budget"},
            "input_space":"srgb", "input_white":"d65", "target_space":"srgb", "target_white":"d65",
            "ridge_lambda":RIDGE_LAMBDA, "mapping":mapping, "warmups":warmups, "blocks":blocks, "runs_per_block":repetitions,
            "patches":patches, "final_model":final_model, "runs":runs, "model_lut_approximation":approximation,
            "limitations":["Synthetic cast of the built-in Classic references; training scores are not independent physical accuracy.",
                "Timing covers only fit_correction_model, including internal NN early-stopping validation where used.",
                "NN uses the recorded epoch/patience budget; no convergence/quality ranking follows from runtime or training scores.",
                "KPLS is unsupported by the current native model enum; PLS uses its existing polynomial features."]})
        );
    }
    Ok(())
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error}");
        println!("{}", json!({"status":"failed", "error":error}));
        std::process::exit(1);
    }
}
