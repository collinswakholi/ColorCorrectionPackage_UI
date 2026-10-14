use ccp_core::ffc::{apply_ffc_float, identity_gain};
use ccp_core::lut::{cube_text, LutPipeline, LUT_NO_FFC_COMMENT};
use ccp_core::refs::{ChartPatch, Vec2};
use ccp_core::stages::{
    apply_gc_image, apply_gc_pixel, extrapolate_saturated, flag_saturated, GammaModel,
};
use ccp_core::{Lab, SATURATION_THRESHOLD};
use ndarray::Array3;

#[test]
fn apply_ffc_float_identity() {
    let mut img = Array3::<f32>::zeros((4, 5, 3));
    for y in 0..4 {
        for x in 0..5 {
            img[[y, x, 0]] = 0.25 + 0.01 * x as f32;
            img[[y, x, 1]] = 0.40;
            img[[y, x, 2]] = 0.55;
        }
    }
    let gain = identity_gain(4, 5);
    let out = apply_ffc_float(&img, &gain);
    for y in 0..4 {
        for x in 0..5 {
            for c in 0..3 {
                let d = (out[[y, x, c]] - img[[y, x, c]]).abs();
                assert!(d < 2e-3, "pixel ({y},{x},{c}) delta {d}");
            }
        }
    }
}

#[test]
fn cube_has_lut_size_and_no_ffc() {
    let text = cube_text("ccp_core test", 5, &LutPipeline::default()).unwrap();
    assert!(text.contains("LUT_3D_SIZE"));
    assert!(text.contains("DOMAIN_MIN"));
    assert!(text.contains("DOMAIN_MAX"));
    assert!(text.contains(LUT_NO_FFC_COMMENT));
    assert!(!text.to_ascii_lowercase().contains("flat field baked"));
    assert!(!text.contains("FFC_GAIN"));
}

fn dummy_patch(index: usize, name: &str, rgb: [f64; 3]) -> ChartPatch {
    ChartPatch {
        name: name.into(),
        index,
        reference_lab_d50: Lab::new(50.0, 0.0, 0.0),
        reference_srgb: [0.5, 0.5, 0.5],
        measured_srgb: rgb,
        original_measured_srgb: rgb,
        center: Vec2 { x: 0.0, y: 0.0 },
        saturated: rgb.iter().any(|&c| c >= SATURATION_THRESHOLD),
    }
}

#[test]
fn sat_flags_stay_on_measured_index() {
    let mut patches = vec![
        dummy_patch(0, "dark skin", [0.2, 0.2, 0.2]),
        dummy_patch(1, "light skin", [0.4, 0.3, 0.3]),
        dummy_patch(2, "blue sky", [0.3, 0.3, 0.4]),
        dummy_patch(3, "foliage", [1.0, 0.4, 0.2]),
    ];
    let before = flag_saturated(&patches, SATURATION_THRESHOLD);
    assert!(!before.flags[0]);
    assert!(before.flags[3]);
    assert_eq!(before.names, vec!["foliage".to_string()]);
    let after = extrapolate_saturated(&mut patches, SATURATION_THRESHOLD, -0.2, 1.3);
    assert_eq!(after.flags.len(), 4);
    assert!(after.flags[3]);
    assert!(!after.flags[0]);
    assert_eq!(patches[3].index, 3);
    assert!(patches[3].original_measured_srgb[0] >= SATURATION_THRESHOLD);
}

#[test]
fn apply_gc_image_matches_pixel() {
    let model = GammaModel::legacy(vec![1.0, 0.0]);
    let mut img = Array3::<f32>::zeros((8, 8, 3));
    for y in 0..8 {
        for x in 0..8 {
            img[[y, x, 0]] = 0.10 + 0.01 * x as f32;
            img[[y, x, 1]] = 0.22;
            img[[y, x, 2]] = 0.33 + 0.01 * y as f32;
        }
    }
    let out = apply_gc_image(&img, &model).unwrap();
    for y in 0..8 {
        for x in 0..8 {
            let rgb = [
                img[[y, x, 0]] as f64,
                img[[y, x, 1]] as f64,
                img[[y, x, 2]] as f64,
            ];
            let expected = apply_gc_pixel(rgb, &model).unwrap();
            for c in 0..3 {
                let d = (out[[y, x, c]] as f64 - expected[c]).abs();
                assert!(d < 1e-5, "pixel ({y},{x},{c}) delta {d}");
            }
        }
    }
}
