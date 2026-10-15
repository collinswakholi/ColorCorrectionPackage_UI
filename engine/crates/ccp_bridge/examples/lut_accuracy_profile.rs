use ccp_bridge::run_pipeline::{load_saved, rgb_image_to_array3};
use ccp_core::color::{delta_e_2000, srgb_to_lab_d65};
use ccp_core::ffc::{apply_ffc_gc_fused, compute_multiplier, FfcFitMethod, FfcFitParams};
use ccp_core::maps::{apply_correction_pixel, build_encoded_lut, sample_encoded_lut};
use ccp_core::sample::load_image;
use ccp_core::stages::apply_wb_inplace;
use serde_json::{json, Value};
use std::path::{Path, PathBuf};
use std::time::Instant;

fn percentile(sorted: &[f64], p: f64) -> f64 {
    let index = ((sorted.len() - 1) as f64 * p).round() as usize;
    sorted[index.min(sorted.len() - 1)]
}

fn summary(mut values: Vec<f64>) -> Value {
    values.sort_by(|a, b| a.total_cmp(b));
    json!({
        "n": values.len(),
        "mean": values.iter().sum::<f64>() / values.len().max(1) as f64,
        "p50": percentile(&values, 0.50),
        "p95": percentile(&values, 0.95),
        "p99": percentile(&values, 0.99),
        "max": values.last().copied(),
    })
}

fn rgb_samples(count: usize) -> Vec<[f64; 3]> {
    let mut state = 0x4d595df4d0f33173u64;
    let mut next = || {
        state = state
            .wrapping_mul(6364136223846793005)
            .wrapping_add(1442695040888963407);
        ((state >> 11) as f64) / ((1u64 << 53) as f64)
    };
    (0..count).map(|_| [next(), next(), next()]).collect()
}

fn compare_samples(samples: &[[f64; 3]], direct: &[[f64; 3]], lut: &[f32], size: usize) -> Value {
    let mut delta_e = Vec::with_capacity(samples.len());
    let mut max_channel = Vec::with_capacity(samples.len());
    for (rgb, expected) in samples.iter().zip(direct.iter()) {
        let actual = sample_encoded_lut(lut, size, *rgb);
        delta_e.push(delta_e_2000(
            srgb_to_lab_d65(actual).expect("LUT Lab"),
            srgb_to_lab_d65(*expected).expect("direct Lab"),
        ));
        max_channel.push(
            actual
                .iter()
                .zip(expected.iter())
                .map(|(a, b)| (a - b).abs())
                .fold(0.0, f64::max),
        );
    }
    json!({
        "delta_e_2000_vs_direct": summary(delta_e),
        "max_encoded_channel_error": summary(max_channel),
    })
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("workspace root");
    let model_ids = [
        "rpcc2-full-ffc-linear",
        "linear-d3-combo",
        "nn-d2-h16-adam",
        "nn-d2-64x32-adam-bn",
        "nn-d2-128x64x32-adam",
    ];
    let samples = rgb_samples(20_000);
    let combo = rgb_image_to_array3(&load_image(root.join("test_images/ColorCard_Combo.JPG"))?)?;
    let white = rgb_image_to_array3(&load_image(root.join("test_images/Blank.JPG"))?)?;
    let ffc = FfcFitParams {
        method: FfcFitMethod::Linear,
        degree: 3,
        bins: 50,
        smooth_window: 5,
        ..FfcFitParams::default()
    };
    let gain = compute_multiplier(Some(&white), Some([390, 235, 5130, 3379]), &ffc)?;
    let base_saved = load_saved(root.join("_ui_stress/matrix/rpcc2-full-ffc-linear/model.json"))?;
    let (base_pipeline, _) = base_saved.lut_pipeline()?;
    let mut pre_cc = apply_ffc_gc_fused(&combo, &gain, base_pipeline.gc.as_ref().expect("GC"));
    apply_wb_inplace(&mut pre_cc, base_pipeline.wb.as_ref().expect("WB"));
    let pixels = pre_cc.len() / 3;
    let mut state = 0xd1342543de82ef95u64;
    let actual_samples: Vec<[f64; 3]> = (0..20_000)
        .map(|_| {
            state = state
                .wrapping_mul(2862933555777941757)
                .wrapping_add(3037000493);
            let index = (state as usize % pixels) * 3;
            let data = pre_cc.as_slice().expect("contiguous image");
            [
                data[index] as f64,
                data[index + 1] as f64,
                data[index + 2] as f64,
            ]
        })
        .collect();
    let mut rows = Vec::new();
    for model_id in model_ids {
        eprintln!("profiling LUT accuracy for {model_id}");
        let path = root.join(format!("_ui_stress/matrix/{model_id}/model.json"));
        let saved = load_saved(&path)?;
        let (pipeline, _) = saved.lut_pipeline()?;
        let (model, settings) = pipeline.cc.as_ref().expect("CC model");
        let direct: Vec<[f64; 3]> = samples
            .iter()
            .map(|&rgb| apply_correction_pixel(model, rgb, settings))
            .collect::<Result<_, _>>()?;
        let actual_direct: Vec<[f64; 3]> = actual_samples
            .iter()
            .map(|&rgb| apply_correction_pixel(model, rgb, settings))
            .collect::<Result<_, _>>()?;
        for size in [17usize, 33usize] {
            let started = Instant::now();
            let lut = build_encoded_lut(model, settings, size)?;
            let build_ms = started.elapsed().as_secs_f64() * 1000.0;
            let uniform_error = compare_samples(&samples, &direct, &lut, size);
            let actual_error = compare_samples(&actual_samples, &actual_direct, &lut, size);
            rows.push(json!({
                "model": model_id,
                "kind": model.kind.name(),
                "hidden_layers": model.hidden_layers,
                "lut_size": size,
                "lut_build_ms": build_ms,
                "uniform_rgb": uniform_error,
                "colorcard_combo_post_ffc_gc_wb": actual_error,
            }));
        }
    }
    let output: PathBuf = root.join("_ui_stress/lut-accuracy-profile.json");
    let report = json!({
        "samples_per_distribution": samples.len(),
        "rows": rows,
        "notes": [
            "Deterministic uniform encoded-sRGB samples.",
            "Compares trilinear CC LUT sampling against direct model evaluation on uniform RGB and actual post-FFC/GC/WB image pixels.",
            "GC, WB, FFC, file I/O, and quantization are excluded."
        ]
    });
    std::fs::write(&output, serde_json::to_string_pretty(&report)?)?;
    println!("{}", serde_json::to_string_pretty(&report)?);
    eprintln!("wrote {}", output.display());
    Ok(())
}
