//! Chart and white-plane detection for ColorCorrect Studio: Opt2 inference,
//! OBB decode and chart assembly, the accelerator probe and the GPU LUT.

#[cfg(feature = "ort-plane")]
mod assemble;
pub mod cache_key;
#[cfg(feature = "ort-plane")]
mod decode;
mod detection;
// Detection geometry and plane post-processing run only with ONNX Runtime;
// without it these modules contribute their result types alone.
#[cfg_attr(not(feature = "ort-plane"), allow(dead_code))]
pub mod geom;
#[cfg(feature = "ort-plane")]
mod gpu_inference;
#[cfg(feature = "ort-plane")]
mod handshake;
#[cfg(feature = "ort-plane")]
mod infer_ort;
pub mod lut_chunks;
pub mod lut_wgpu;
pub mod paths;
#[cfg_attr(not(feature = "ort-plane"), allow(dead_code))]
pub mod plane;
pub mod probe;
#[cfg(any(feature = "wgpu-lut", feature = "ort-plane"))]
mod wgpu_instance;

use std::path::Path;

pub use detection::{DetectResult, DetectSettings};
pub use geom::{DEFAULT_CONF, DEFAULT_CROP};
#[cfg(feature = "ort-plane")]
pub use infer_ort::{inference_records, last_detect_ep};

/// Initialize the chart detector for `settings`.
pub fn warmup_detector(settings: &DetectSettings) -> DetectResult {
    #[cfg(feature = "ort-plane")]
    {
        infer_ort::warmup_detector_ort(settings)
    }
    #[cfg(not(feature = "ort-plane"))]
    {
        let _ = settings;
        inference_disabled()
    }
}

/// Detect the charts in the image at `path`.
pub fn detect_chart(path: &Path, settings: &DetectSettings) -> DetectResult {
    #[cfg(feature = "ort-plane")]
    {
        infer_ort::detect_chart_ort(path, settings)
    }
    #[cfg(not(feature = "ort-plane"))]
    {
        let _ = (path, settings);
        inference_disabled()
    }
}

#[cfg(not(feature = "ort-plane"))]
fn inference_disabled() -> DetectResult {
    DetectResult {
        error: "Chart inference requires the ort-plane build feature.".into(),
        error_code: "unsupported_capability".into(),
        ..DetectResult::default()
    }
}

/// Explicit retry drops sessions without unregistering native plugins still owned by ORT.
pub fn reset_inference_sessions() {
    #[cfg(feature = "ort-plane")]
    {
        infer_ort::clear_sessions();
        plane::clear_session();
        gpu_inference::reset_failures();
    }
}

/// Release native sessions and GPU plugin resources before process teardown.
pub fn shutdown_inference() -> Result<(), String> {
    #[cfg(feature = "ort-plane")]
    {
        infer_ort::clear_sessions();
        plane::clear_session();
        gpu_inference::shutdown()?;
    }
    Ok(())
}

#[cfg(all(test, feature = "ort-plane"))]
mod assemble_geom_tests;
