use ccp_bridge::run_pipeline::rgb_image_to_array3;
use ccp_core::color::srgb_lstar_d65;
use ccp_core::ffc::{apply_ffc_float, compute_multiplier, FfcFitMethod, FfcFitParams};
use ccp_core::sample::load_image;
use ndarray::{Array2, Array3};
use serde_json::{json, Value};
use std::path::{Path, PathBuf};
use std::time::Instant;

fn percentile(sorted: &[f64], p: f64) -> f64 {
    if sorted.is_empty() {
        return f64::NAN;
    }
    let index = ((sorted.len() - 1) as f64 * p).round() as usize;
    sorted[index.min(sorted.len() - 1)]
}

fn lstar_stats(image: &Array3<f32>, rect: [i32; 4]) -> Value {
    let [x, y, width, height] = rect;
    let x0 = x.max(0) as usize;
    let y0 = y.max(0) as usize;
    let x1 = (x + width).max(0).min(image.shape()[1] as i32) as usize;
    let y1 = (y + height).max(0).min(image.shape()[0] as i32) as usize;
    let stride = 8usize;
    let mut values = Vec::with_capacity(((x1 - x0) / stride + 1) * ((y1 - y0) / stride + 1));
    for yy in (y0..y1).step_by(stride) {
        for xx in (x0..x1).step_by(stride) {
            values.push(srgb_lstar_d65([
                image[[yy, xx, 0]] as f64,
                image[[yy, xx, 1]] as f64,
                image[[yy, xx, 2]] as f64,
            ]));
        }
    }
    let mean = values.iter().sum::<f64>() / values.len().max(1) as f64;
    let variance = values
        .iter()
        .map(|value| (value - mean).powi(2))
        .sum::<f64>()
        / values.len().max(1) as f64;
    values.sort_by(|a, b| a.total_cmp(b));
    let p05 = percentile(&values, 0.05);
    let p95 = percentile(&values, 0.95);
    json!({
        "samples": values.len(),
        "mean_lstar": mean,
        "std_lstar": variance.sqrt(),
        "cv_percent": variance.sqrt() / mean.max(1e-12) * 100.0,
        "p05_lstar": p05,
        "p95_lstar": p95,
        "p95_p05_lstar": p95 - p05,
    })
}

fn gain_stats(gain: &Array2<f32>) -> Value {
    let mut values: Vec<f64> = gain.iter().step_by(64).map(|value| *value as f64).collect();
    values.sort_by(|a, b| a.total_cmp(b));
    let mean = values.iter().sum::<f64>() / values.len().max(1) as f64;
    json!({
        "samples": values.len(),
        "mean": mean,
        "min": values.first().copied(),
        "p05": percentile(&values, 0.05),
        "p95": percentile(&values, 0.95),
        "max": values.last().copied(),
    })
}

fn timing(times: &[f64]) -> Value {
    let mut sorted = times.to_vec();
    sorted.sort_by(|a, b| a.total_cmp(b));
    json!({
        "n": sorted.len(),
        "mean_ms": sorted.iter().sum::<f64>() / sorted.len().max(1) as f64,
        "p50_ms": percentile(&sorted, 0.5),
        "min_ms": sorted.first().copied(),
        "max_ms": sorted.last().copied(),
        "times_ms": sorted,
    })
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("workspace root");
    let white_path = root.join("test_images/Blank.JPG");
    let output_path: PathBuf = root.join("_ui_stress/ffc-uniformity-profile.json");
    let white = rgb_image_to_array3(&load_image(&white_path)?)?;
    // This is the plane rectangle reported by the current detector for Blank.JPG.
    let rect = [390, 235, 5130, 3379];
    let baseline = lstar_stats(&white, rect);
    let methods = [
        ("linear", FfcFitMethod::Linear),
        ("poly", FfcFitMethod::Poly),
        ("pls", FfcFitMethod::Pls),
        ("nn", FfcFitMethod::Nn),
    ];
    let mut rows = Vec::new();
    for (name, method) in methods {
        eprintln!("profiling FFC {name}");
        let params = FfcFitParams {
            method,
            degree: 3,
            bins: 50,
            smooth_window: 5,
            nn_max_iter: 10_000,
            ..FfcFitParams::default()
        };
        let mut fit_ms = Vec::new();
        let mut apply_ms = Vec::new();
        let mut last_gain = None;
        let mut last_corrected = None;
        for _ in 0..3 {
            let start = Instant::now();
            let gain = compute_multiplier(Some(&white), Some(rect), &params)?;
            fit_ms.push(start.elapsed().as_secs_f64() * 1000.0);
            let start = Instant::now();
            let corrected = apply_ffc_float(&white, &gain);
            apply_ms.push(start.elapsed().as_secs_f64() * 1000.0);
            last_gain = Some(gain);
            last_corrected = Some(corrected);
        }
        rows.push(json!({
            "method": name,
            "fit": timing(&fit_ms),
            "apply": timing(&apply_ms),
            "gain": gain_stats(last_gain.as_ref().expect("gain")),
            "corrected": lstar_stats(last_corrected.as_ref().expect("corrected"), rect),
        }));
    }
    let report = json!({
        "white": white_path,
        "pixels": [white.shape()[1], white.shape()[0]],
        "plane": rect,
        "baseline": baseline,
        "methods": rows,
        "notes": [
            "In-process release timings; decoded Blank.JPG is reused.",
            "Each method runs three full gain fits and three full FFC applies.",
            "Uniformity is measured from every eighth pixel inside the detected plane."
        ]
    });
    std::fs::write(&output_path, serde_json::to_string_pretty(&report)?)?;
    println!("{}", serde_json::to_string_pretty(&report)?);
    eprintln!("wrote {}", output_path.display());
    Ok(())
}
