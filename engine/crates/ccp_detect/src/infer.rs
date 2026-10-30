//! ONNX chart inference using explicit GPU providers or the CPU backend.
use std::path::Path;
use crate::{DetectResult, DetectSettings};
pub fn warmup_detector(settings: &DetectSettings) -> DetectResult {
    #[cfg(feature = "ort-plane")]
    { crate::infer_ort::warmup_detector_ort(settings) }
    #[cfg(not(feature = "ort-plane"))]
    { let _ = settings; disabled() }
}
pub fn detect_chart(path: &Path, settings: &DetectSettings) -> DetectResult {
    #[cfg(feature = "ort-plane")]
    { crate::infer_ort::detect_chart_ort(path, settings) }
    #[cfg(not(feature = "ort-plane"))]
    { let _ = (path, settings); disabled() }
}
#[cfg(not(feature = "ort-plane"))]
fn disabled() -> DetectResult {
    DetectResult { error: "Chart inference requires the ort-plane build feature.".into(), ..DetectResult::default() }
}
