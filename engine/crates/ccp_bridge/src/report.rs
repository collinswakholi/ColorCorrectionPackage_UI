//! CIEDE2000 chart metrics of a fit, computed from already-sampled patches:
//! Lab on tens of patches, never another full-image pass.

use ccp_core::color::{convert_encoded_rgb, srgb_d65_space, RgbColorSpace};
use ccp_core::honest::delta_e00_in_space;
use ccp_core::refs::ChartPatch;
use serde_json::{json, Value};

/// Mean, median and max ΔE; NaN when nothing was measured.
pub(crate) type Stats = (f64, f64, f64);

pub(crate) const UNMEASURED: Stats = (f64::NAN, f64::NAN, f64::NAN);

fn mean(values: &[f64]) -> f64 {
    if values.is_empty() {
        f64::NAN
    } else {
        values.iter().sum::<f64>() / values.len() as f64
    }
}

fn max(values: &[f64]) -> f64 {
    values.iter().copied().fold(f64::NAN, f64::max)
}

/// Zero for no values (the report's historical median).
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

/// ΔE of the patches' current values (or, with `original`, their captured
/// values) against their references, measured in `space`.
pub(crate) fn patch_stats(patches: &[ChartPatch], original: bool, space: &RgbColorSpace) -> Stats {
    let values: Vec<f64> = patches
        .iter()
        .filter_map(|p| {
            let rgb = if original {
                p.original_measured_srgb
            } else {
                p.measured_srgb
            };
            delta_e00_in_space(rgb, p.reference_lab_d50, space).ok()
        })
        .filter(|de| de.is_finite())
        .collect();
    let (mean, max) = (mean(&values), max(&values));
    let median = if values.is_empty() {
        f64::NAN
    } else {
        median(values)
    };
    (mean, median, max)
}

pub(crate) fn stage_json(name: &str, before: Stats, after: Stats, elapsed: f64) -> Value {
    let improve = if before.0.is_finite() && after.0.is_finite() && before.0 > 1e-12 {
        (before.0 - after.0) / before.0 * 100.0
    } else {
        0.0
    };
    json!({
        "name": name,
        "mean_before": before.0,
        "mean_after": after.0,
        "median_before": before.1,
        "median_after": after.1,
        "max_before": before.2,
        "max_after": after.2,
        "improvement_percent": improve,
        "elapsed_secs": elapsed,
    })
}

/// Per-patch and overall ΔE of the captured (`before`, in `input_space`) and
/// corrected (`after`, in `output_space`) chart. Without a chart the metrics
/// are hidden rather than invented.
pub(crate) fn build_report(
    model_name: &str,
    feature_count: i32,
    before: &[ChartPatch],
    after: &[ChartPatch],
    has_chart: bool,
    input_space: &RgbColorSpace,
    output_space: &RgbColorSpace,
) -> Value {
    if !has_chart || before.is_empty() {
        return json!({
            "model": model_name,
            "feature_count": feature_count,
            "saturated_patches": 0,
            "metrics_hidden": true,
        });
    }
    let mut patches = Vec::new();
    let mut before_v = Vec::new();
    let mut after_v = Vec::new();
    let display_space = srgb_d65_space().1;
    for (b, a) in before.iter().zip(after.iter()) {
        let db = delta_e00_in_space(b.original_measured_srgb, b.reference_lab_d50, input_space)
            .unwrap_or(f64::NAN);
        let da = delta_e00_in_space(a.measured_srgb, a.reference_lab_d50, output_space)
            .unwrap_or(f64::NAN);
        if db.is_finite() {
            before_v.push(db);
        }
        if da.is_finite() {
            after_v.push(da);
        }
        patches.push(json!({
            "name": b.name,
            "index": b.index,
            "provenance": b.provenance,
            "reference_lab_d50": b.reference_lab_d50,
            "before_delta_e": db,
            "after_delta_e": da,
            "saturated": b.saturated,
            "measured_srgb": b.original_measured_srgb,
            "reference_srgb": b.reference_srgb,
            "corrected_srgb": a.measured_srgb,
            "measured_display_srgb": convert_encoded_rgb(
                b.original_measured_srgb, input_space, display_space).ok(),
            "reference_display_srgb": b.reference_srgb,
            "corrected_display_srgb": convert_encoded_rgb(
                a.measured_srgb, output_space, display_space).ok(),
        }));
    }
    json!({
        "model": model_name,
        "feature_count": feature_count,
        "saturated_patches": before.iter().filter(|p| p.saturated).count(),
        "metrics_hidden": false,
        "colorimetry": {
            "metric": "CIEDE2000", "evaluation_white": "d65", "observer_degrees": 2,
            "reference_white": "d50", "chromatic_adaptation": "Bradford",
            "input_space": input_space.id, "input_white": input_space.white.id,
            "output_space": output_space.id, "output_white": output_space.white.id,
            "rgb_fields_note": "Legacy measured_srgb/corrected_srgb contain declared input/output RGB; reference_srgb and all *_display_srgb fields are display sRGB/D65",
            "display_space": "srgb", "display_white": "d65"
        },
        "delta_e_2000": {
            "mean_before": mean(&before_v),
            "mean_after": mean(&after_v),
            "median_before": median(before_v.clone()),
            "median_after": median(after_v.clone()),
            "max_before": max(&before_v),
            "max_after": max(&after_v),
        },
        "patches": patches,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use ccp_core::color::{lab_d50_to_srgb, lab_d50_to_target_linear, linear_to_encoded_rgb, Lab};
    use ccp_core::refs::{chart_spec_from_name, Vec2};

    use crate::model::correction_settings;

    #[test]
    fn reports_use_declared_input_and_output_spaces() {
        let spec = chart_spec_from_name("classic").unwrap();
        let sample = Lab::new(60.0, 5.0, -5.0);
        let base = ChartPatch {
            provenance: None,
            name: spec.names[0].clone(),
            index: 0,
            reference_lab_d50: sample,
            reference_srgb: lab_d50_to_srgb(sample).unwrap(),
            measured_srgb: [0.0; 3],
            original_measured_srgb: [0.0; 3],
            center: Vec2 { x: 0.0, y: 0.0 },
            saturated: false,
        };
        for input in ccp_core::color::supported_rgb_color_spaces() {
            for output in ccp_core::color::supported_rgb_color_spaces() {
                let native_white = |name: &str| match name {
                    "prophoto-rgb" => "d50",
                    "acescg" => "d60",
                    _ => "d65",
                };
                let settings =
                    correction_settings(input, native_white(input), output, native_white(output))
                        .unwrap();
                let encode = |space: &RgbColorSpace| {
                    linear_to_encoded_rgb(
                        lab_d50_to_target_linear(sample, space, &space.white).unwrap(),
                        space,
                    )
                };
                let mut before = base.clone();
                before.original_measured_srgb = encode(&settings.input_space);
                before.measured_srgb = before.original_measured_srgb;
                let mut after = base.clone();
                after.measured_srgb = encode(&settings.target_space);
                let raw_before = before.original_measured_srgb;
                let raw_after = after.measured_srgb;
                let report = build_report(
                    "linear",
                    3,
                    &[before],
                    &[after],
                    true,
                    &settings.input_space,
                    &settings.target_space,
                );
                assert!(
                    report["delta_e_2000"]["mean_before"].as_f64().unwrap() < 1e-10,
                    "{input}->{output}"
                );
                assert!(
                    report["delta_e_2000"]["mean_after"].as_f64().unwrap() < 1e-10,
                    "{input}->{output}"
                );
                assert_eq!(
                    report["colorimetry"]["output_space"],
                    settings.target_space.id
                );
                let patch = &report["patches"][0];
                // Preserve raw numeric fields for API consumers and old reports.
                assert_eq!(patch["measured_srgb"], json!(raw_before));
                assert_eq!(patch["corrected_srgb"], json!(raw_after));
                for field in [
                    "measured_display_srgb",
                    "reference_display_srgb",
                    "corrected_display_srgb",
                ] {
                    let display = patch[field].as_array().unwrap();
                    for (channel, value) in display.iter().enumerate() {
                        assert!(
                            (value.as_f64().unwrap() - base.reference_srgb[channel]).abs() < 1e-10,
                            "{input}->{output}: {field} must represent the same display color"
                        );
                    }
                }
            }
        }
    }
}
