//! Runtime/model identity shared by acceleration caches.
use std::path::Path;

/// Metadata invalidation avoids retaining a session after a model or its external
/// tensor payload is replaced. Reproducible benchmarks additionally hash assets.
pub fn model_cache_identity(path: &Path) -> String {
    fn stamp(path: &Path) -> String {
        match std::fs::metadata(path) {
            Ok(m) => format!("{}:{:?}", m.len(), m.modified().ok()),
            Err(_) => "missing".into(),
        }
    }
    let canonical = path.canonicalize().unwrap_or_else(|_| path.to_path_buf());
    format!(
        "{}:{}:{}",
        canonical.display(),
        stamp(path),
        stamp(&path.with_extension("onnx.data"))
    )
}

pub fn runtime_cache_identity() -> String {
    format!(
        "{}:{}:{}:{}:{}:{}:{}",
        crate::runtime_policy().as_str(),
        serde_json::to_string(&crate::runtime_assignment()).unwrap_or_default(),
        std::env::var("ORT_DYLIB_PATH").unwrap_or_default(),
        std::env::var("CCP_ORT_DEVICE_ID").unwrap_or_default(),
        std::env::var("CCP_WEBGPU_LIBRARY").unwrap_or_default(),
        std::env::var("CCP_WGPU_INFERENCE_ADAPTER").unwrap_or_default(),
        { #[cfg(feature="ort-plane")] {crate::gpu_inference::component_policy_identity()}
          #[cfg(not(feature="ort-plane"))] {String::new()} }
    )
}

#[cfg(feature = "ort-plane")]
pub(crate) fn ort_device_id() -> Result<i32, String> {
    let value = std::env::var("CCP_ORT_DEVICE_ID").unwrap_or_else(|_| "0".into());
    value
        .parse::<i32>()
        .ok()
        .filter(|v| *v >= 0)
        .ok_or_else(|| "CCP_ORT_DEVICE_ID must be a nonnegative adapter index".into())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn replacing_model_invalidates_identity() {
        let path = std::env::temp_dir().join(format!("ccp-cache-{}.onnx", std::process::id()));
        std::fs::write(&path, b"first").unwrap();
        let first = model_cache_identity(&path);
        std::fs::write(&path, b"replacement").unwrap();
        assert_ne!(first, model_cache_identity(&path));
        std::fs::remove_file(path).unwrap();
    }
}
