//! Opt-in GPU integration. An explicitly requested run requires its resources.

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
#[ignore = "requires an explicit compatible GPU runtime; run with --features ort-plane -- --ignored"]
fn opt2_onnx_detect_gpu_smoke() {
    let root = find_ccp_new_ui_root().expect("GPU smoke test requires a resource root");
    let deploy = root.join("models").join("opt2");
    let onnx = primary_onnx_path(&deploy);
    assert!(onnx.is_file(), "GPU smoke test requires {}", onnx.display());
    let image =
        test_image().expect("GPU smoke test requires a public ColorCard fixture in test_images");
    let report = probe_accelerators("auto");
    set_runtime_from_report(&report);
    let settings = DetectSettings {
        deploy_dir: deploy,
        device: "auto".into(),
        ..DetectSettings::default()
    };
    let warm = warmup_detector(&settings);
    assert!(warm.ok, "warmup failed: {}", warm.error);
    let result = detect_chart(&image, &settings);
    assert!(result.ok, "detect failed: {}", result.error);
    assert!(result.n_charts > 0, "expected charts");
}
