#![allow(clippy::needless_range_loop)]

use ccp_bridge::run_pipeline::{rgb_image_into_array3, write_still};
use ccp_core::color::{linear_to_srgb, srgb_to_lab_d65, srgb_to_linear};
use ccp_core::ffc::{apply_ffc_inplace, compute_multiplier, FfcFitMethod, FfcFitParams};
use ccp_core::sample::load_image;
use ndarray::{Array2, Array3};
use serde_json::{json, Value};
use std::path::PathBuf;

const PLANE: [usize; 4] = [390, 235, 5130, 3379];
const BINS: usize = 50;

fn percentile(mut values: Vec<f64>, q: f64) -> f64 {
    values.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    values[((values.len() - 1) as f64 * q).round() as usize]
}

fn surface(image: &Array3<f32>) -> [Array2<f64>; 3] {
    let [x0, y0, width, height] = PLANE;
    let mut sums = [
        Array2::zeros((BINS, BINS)),
        Array2::zeros((BINS, BINS)),
        Array2::zeros((BINS, BINS)),
    ];
    let mut counts = Array2::<f64>::zeros((BINS, BINS));
    for y in (y0..y0 + height).step_by(3) {
        for x in (x0..x0 + width).step_by(3) {
            let by = ((y - y0) * BINS / height).min(BINS - 1);
            let bx = ((x - x0) * BINS / width).min(BINS - 1);
            let linear = srgb_to_linear([
                image[[y, x, 0]] as f64,
                image[[y, x, 1]] as f64,
                image[[y, x, 2]] as f64,
            ]);
            for channel in 0..3 {
                sums[channel][[by, bx]] += linear[channel];
            }
            counts[[by, bx]] += 1.0;
        }
    }
    for channel in 0..3 {
        for by in 0..BINS {
            for bx in 0..BINS {
                sums[channel][[by, bx]] /= counts[[by, bx]].max(1.0);
            }
        }
        let original = sums[channel].clone();
        for by in 0..BINS {
            for bx in 0..BINS {
                let mut total = 0.0;
                let mut n = 0.0;
                for yy in by.saturating_sub(2)..=(by + 2).min(BINS - 1) {
                    for xx in bx.saturating_sub(2)..=(bx + 2).min(BINS - 1) {
                        total += original[[yy, xx]];
                        n += 1.0;
                    }
                }
                sums[channel][[by, bx]] = total / n;
            }
        }
    }
    sums
}

fn gain_surfaces(surfaces: &[Array2<f64>; 3]) -> [Array2<f64>; 3] {
    std::array::from_fn(|channel| {
        let target = percentile(surfaces[channel].iter().copied().collect(), 0.95);
        surfaces[channel].mapv(|value| (target / value.max(1e-8)).clamp(0.5, 1.8))
    })
}

fn bilinear(grid: &Array2<f64>, x: usize, y: usize) -> f64 {
    let [x0, y0, width, height] = PLANE;
    let gx = (x.saturating_sub(x0).min(width - 1)) as f64 * (BINS - 1) as f64 / (width - 1) as f64;
    let gy =
        (y.saturating_sub(y0).min(height - 1)) as f64 * (BINS - 1) as f64 / (height - 1) as f64;
    let x1 = gx.floor() as usize;
    let y1 = gy.floor() as usize;
    let x2 = (x1 + 1).min(BINS - 1);
    let y2 = (y1 + 1).min(BINS - 1);
    let tx = gx - x1 as f64;
    let ty = gy - y1 as f64;
    let top = grid[[y1, x1]] * (1.0 - tx) + grid[[y1, x2]] * tx;
    let bottom = grid[[y2, x1]] * (1.0 - tx) + grid[[y2, x2]] * tx;
    top * (1.0 - ty) + bottom * ty
}

fn metrics(image: &Array3<f32>, gains: Option<&[Array2<f64>; 3]>) -> Value {
    let [x0, y0, width, height] = PLANE;
    let mut channels = [Vec::new(), Vec::new(), Vec::new()];
    let mut lightness = Vec::new();
    let mut a = Vec::new();
    let mut b = Vec::new();
    for y in (y0..y0 + height).step_by(8) {
        for x in (x0..x0 + width).step_by(8) {
            let encoded = [
                image[[y, x, 0]] as f64,
                image[[y, x, 1]] as f64,
                image[[y, x, 2]] as f64,
            ];
            let mut linear = srgb_to_linear(encoded);
            if let Some(gain) = gains {
                for channel in 0..3 {
                    linear[channel] *= bilinear(&gain[channel], x, y);
                }
            }
            let corrected = linear_to_srgb(linear.map(|value| value.clamp(0.0, 1.0)));
            for channel in 0..3 {
                channels[channel].push(linear[channel]);
            }
            let lab = srgb_to_lab_d65(corrected).expect("valid Lab");
            lightness.push(lab.l);
            a.push(lab.a);
            b.push(lab.b);
        }
    }
    let stat = |values: &[f64]| {
        let mean = values.iter().sum::<f64>() / values.len() as f64;
        let std = (values
            .iter()
            .map(|value| (value - mean).powi(2))
            .sum::<f64>()
            / values.len() as f64)
            .sqrt();
        (mean, std)
    };
    let (l_mean, l_std) = stat(&lightness);
    let (_, a_std) = stat(&a);
    let (_, b_std) = stat(&b);
    let channel_cv: Vec<f64> = channels
        .iter()
        .map(|values| {
            let (mean, std) = stat(values);
            std / mean.max(1e-12) * 100.0
        })
        .collect();
    json!({
        "lstar_cv_percent": l_std / l_mean * 100.0,
        "lstar_std": l_std,
        "a_std": a_std,
        "b_std": b_std,
        "linear_rgb_cv_percent": channel_cv,
    })
}

fn apply_rgb_gains(image: &mut Array3<f32>, gains: &[Array2<f64>; 3]) {
    let (height, width, _) = image.dim();
    for y in 0..height {
        for x in 0..width {
            let mut linear = srgb_to_linear([
                image[[y, x, 0]] as f64,
                image[[y, x, 1]] as f64,
                image[[y, x, 2]] as f64,
            ]);
            for channel in 0..3 {
                linear[channel] *= bilinear(&gains[channel], x, y);
            }
            let encoded = linear_to_srgb(linear.map(|value| value.clamp(0.0, 1.0)));
            for channel in 0..3 {
                image[[y, x, channel]] = encoded[channel] as f32;
            }
        }
    }
}

fn noise_average_probe(base: &[Array2<f64>; 3]) -> Value {
    let base_gain = gain_surfaces(base);
    let mut state = 0x9e3779b97f4a7c15u64;
    let mut noisy = std::array::from_fn(|_| Array2::<f64>::zeros((BINS, BINS)));
    let mut averaged = std::array::from_fn(|_| Array2::<f64>::zeros((BINS, BINS)));
    for frame in 0..8 {
        for channel in 0..3 {
            for y in 0..BINS {
                for x in 0..BINS {
                    state = state.wrapping_mul(6364136223846793005).wrapping_add(1);
                    let unit = ((state >> 11) as f64) / ((1u64 << 53) as f64);
                    let value = base[channel][[y, x]] * (1.0 + (unit - 0.5) * 0.04);
                    if frame == 0 {
                        noisy[channel][[y, x]] = value;
                    }
                    averaged[channel][[y, x]] += value / 8.0;
                }
            }
        }
    }
    let rmse = |candidate: &[Array2<f64>; 3]| {
        let candidate_gain = gain_surfaces(candidate);
        let mut sum = 0.0;
        let mut n = 0usize;
        for channel in 0..3 {
            for (actual, expected) in candidate_gain[channel]
                .iter()
                .zip(base_gain[channel].iter())
            {
                sum += (actual - expected).powi(2);
                n += 1;
            }
        }
        (sum / n as f64).sqrt()
    };
    let single = rmse(&noisy);
    let average = rmse(&averaged);
    json!({
        "synthetic_noise_peak_to_peak_percent": 4.0,
        "frames": 8,
        "single_gain_rmse": single,
        "averaged_gain_rmse": average,
        "rmse_reduction_percent": (single - average) / single * 100.0,
    })
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let blank = rgb_image_into_array3(load_image(root.join("test_images/Blank.JPG"))?)?;
    let surfaces = surface(&blank);
    let gains = gain_surfaces(&surfaces);
    let prototype_dir = root.join("_ui_stress/option2-prototype");
    std::fs::create_dir_all(&prototype_dir)?;
    let mut target =
        rgb_image_into_array3(load_image(root.join("test_images/ColorCard_Big.JPG"))?)?;
    apply_rgb_gains(&mut target, &gains);
    let target_path = prototype_dir.join("ColorCard_Big_rgb_ffc.png");
    write_still(&target, &target_path)?;

    let mut lstar_corrected = blank.clone();
    let lstar_gain = compute_multiplier(
        Some(&blank),
        Some(PLANE.map(|value| value as i32)),
        &FfcFitParams {
            method: FfcFitMethod::Poly,
            degree: 5,
            ..FfcFitParams::default()
        },
    )?;
    apply_ffc_inplace(&mut lstar_corrected, &lstar_gain);

    let report = json!({
        "corpus": "test_images",
        "limitations": {
            "real_blank_frames": 1,
            "real_dark_frames": 0,
            "dark_frame_testable": false,
            "multi_frame_result_is_synthetic": true,
        },
        "blank_uniformity": {
            "uncorrected": metrics(&blank, None),
            "production_lstar_poly": metrics(&lstar_corrected, None),
            "prototype_per_channel_linear_rgb": metrics(&blank, Some(&gains)),
        },
        "multi_blank_noise_probe": noise_average_probe(&surfaces),
        "prototype_target": target_path,
    });
    let path = root.join("_ui_stress/option2-calibration-probe.json");
    std::fs::write(&path, serde_json::to_string_pretty(&report)?)?;
    println!("{}", serde_json::to_string_pretty(&report)?);
    println!("WROTE {}", path.display());
    Ok(())
}
