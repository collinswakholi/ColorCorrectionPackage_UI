//! Opt2 ONNX detect integration (skipped when weights or test image are absent).

use std::path::PathBuf;

use ccp_detect::paths::{find_ccp_new_ui_root, primary_onnx_path};
use ccp_detect::probe::{probe_accelerators, set_runtime_from_report};
use ccp_detect::{detect_chart, warmup_detector, DetectSettings};

fn test_image() -> Option<PathBuf> {
    let root = find_ccp_new_ui_root()?;
    for rel in [
        "test_images/ColorCard_Combo.JPG",
        "test_images/ColorCard_Big.JPG",
    ] {
        let p = root.join(rel);
        if p.is_file() {
            return Some(p);
        }
    }
    None
}

#[test]
fn opt2_onnx_detect_cpu_smoke() {
    let root = match find_ccp_new_ui_root() {
        Some(r) => r,
        None => return,
    };
    let deploy = root.join("models").join("opt2");
    let onnx = primary_onnx_path(&deploy);
    if !onnx.is_file() {
        eprintln!(
            "skip opt2_onnx_detect_cpu_smoke: missing {}",
            onnx.display()
        );
        return;
    }
    let image = match test_image() {
        Some(p) => p,
        None => {
            eprintln!("skip opt2_onnx_detect_cpu_smoke: no ColorCard test image");
            return;
        }
    };
    let report = probe_accelerators("cpu");
    set_runtime_from_report(&report);
    let settings = DetectSettings {
        deploy_dir: deploy,
        device: "cpu".into(),
        ..DetectSettings::default()
    };
    let warm = warmup_detector(&settings);
    assert!(warm.ok, "warmup failed: {}", warm.error);
    let result = detect_chart(&image, &settings);
    assert!(result.ok, "detect failed: {}", result.error);
    assert!(result.n_charts > 0, "expected charts");
}
