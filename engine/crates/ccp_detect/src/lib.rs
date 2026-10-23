//! Speedy Opt2 assemble, OBB decode, plane crop, and accelerator probe.

pub mod assemble;
pub mod cache_key;
pub mod decode;
pub mod geom;
#[cfg(feature = "ort-plane")]
pub mod gpu_inference;
pub mod handshake;
pub mod infer;
#[cfg(feature = "ort-plane")]
pub mod infer_ort;
pub mod lut_chunks;
pub mod lut_wgpu;
pub mod paths;
pub mod plane;
pub mod probe;

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

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

pub use assemble::{assemble_charts, assemble_one, assemble_type, nms_charts};
pub use decode::{
    covariance, decode_obb, decode_obb_flat, dets_from_raw, letterbox_rgb, preds_to_obb_rows,
    probiou, Letterbox, RawDet,
};
pub use geom::{
    aabb_iou, anchors_for, canonical_center, chart_index, class_id, cli_chart_from_detector,
    format_quad, homography, homography_4, lattice_plausible, order_visual_quad, parse_chart_kind,
    scale_quad, warp, ChartDetection, ChartKind, Det, Quad, Vec2, ANCHOR_NAMES, CHART_COLS,
    CHART_ROWS, CHART_TYPES, DECODE_IOU, DEFAULT_CONF, DEFAULT_CROP, DEFAULT_MAX_DET,
    LETTERBOX_PAD, NMS_CHARTS_IOU, NUM_CLASSES, PRIMARY_SIZE, RECALL_SIZE,
};
pub use handshake::make_handshake;
pub use infer::{detect_chart, warmup_detector};
#[cfg(feature = "ort-plane")]
pub use infer_ort::last_detect_ep;
pub use paths::{default_deploy_dir, find_ccp_new_ui_root};
pub use plane::{
    last_plane_ep, parse_crop_rect, plane_or_full, Rect as PlaneRect, PLANE_CONF, PLANE_INPUT,
    PLANE_NMS, PLANE_SHRINK,
};
pub use probe::{
    apply_thread_count, probe_accelerators, runtime_assignment, runtime_policy,
    set_runtime_from_report, AcceleratorReport, BackendKind, ComputeAssignment, CpuInfo,
    DevicePolicy, GpuInfo,
};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DetectSettings {
    pub deploy_dir: PathBuf,
    pub device: String,
    pub prefer: String,
    pub conf: f32,
    pub iou: f32,
    pub max_det: i32,
    pub crop: f64,
    /// When true (C++ default), run 1536 if 1280 assemble is empty.
    pub allow_recall: bool,
}

impl Default for DetectSettings {
    fn default() -> Self {
        Self {
            deploy_dir: PathBuf::new(),
            device: "gpu".into(),
            prefer: String::new(),
            conf: DEFAULT_CONF,
            iou: DECODE_IOU,
            max_det: DEFAULT_MAX_DET,
            crop: DEFAULT_CROP,
            allow_recall: true,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DetectResult {
    pub ok: bool,
    pub error: String,
    pub json: String,
    pub detection: ChartDetection,
    pub charts: Vec<ChartDetection>,
    pub cli_chart: String,
    pub quad: String,
    pub chart_type: String,
    pub recall_source: String,
    pub backend: String,
    pub runtime: String,
    pub device: String,
    pub n_dets: i32,
    pub n_charts: i32,
}

impl Default for DetectResult {
    fn default() -> Self {
        Self {
            ok: false,
            error: String::new(),
            json: String::new(),
            detection: ChartDetection::default(),
            charts: Vec::new(),
            cli_chart: String::new(),
            quad: String::new(),
            chart_type: String::new(),
            recall_source: "none".into(),
            backend: "onnx".into(),
            runtime: "ort".into(),
            device: String::new(),
            n_dets: 0,
            n_charts: 0,
        }
    }
}
