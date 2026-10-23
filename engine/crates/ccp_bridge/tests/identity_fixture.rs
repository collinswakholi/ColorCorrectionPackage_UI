//! The public CPU-only CI fixture must really preserve pixels without a GPU.
use std::path::Path;

use ccp_bridge::run_pipeline::{
    apply_saved_to_work, last_apply_backend, load_saved, set_cpu_lut_request, ApplyOptions,
};
use ndarray::Array3;

#[test]
fn public_identity_fixture_preserves_pixels_in_both_cpu_methods() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../../testdata/acceptance/identity-model.json");
    let saved = load_saved(path).expect("the public identity fixture must load");
    assert!(!saved.do_ffc && !saved.do_gc && !saved.do_wb && !saved.do_cc);
    assert!(saved.correction.is_none());
    assert!(saved.gc_coeffs.is_none() && saved.gc_model.is_none() && saved.wb_diag.is_none());
    let original = Array3::from_shape_vec(
        (2, 3, 3),
        vec![
            0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 0.03, 0.5, 0.9, 0.73, 0.11, 0.26, 1.0, 0.0, 0.25, 0.18,
            0.18, 0.18,
        ],
    )
    .unwrap();
    set_cpu_lut_request(true);
    for fast_mode in [false, true] {
        let mut image = original.clone();
        let options = ApplyOptions {
            fast_mode,
            ..ApplyOptions::from_saved(&saved)
        };
        apply_saved_to_work(&mut image, &saved, &options, None).unwrap();
        for (&before, &after) in original.iter().zip(image.iter()) {
            assert!(after.is_finite() && (before - after).abs() <= 1e-6);
        }
        assert_eq!(last_apply_backend(), "cpu");
    }
    set_cpu_lut_request(false);
}
