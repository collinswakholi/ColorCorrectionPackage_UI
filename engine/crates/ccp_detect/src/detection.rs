//! The settings a chart detection runs with and the result it returns.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

use crate::geom::{ChartDetection, DECODE_IOU, DEFAULT_CONF, DEFAULT_CROP, DEFAULT_MAX_DET};

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
    #[serde(default)]
    pub inference: serde_json::Value,
    pub n_dets: i32,
    pub n_charts: i32,
    /// Stable protocol error code for a failed result (empty when `ok`).
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub error_code: String,
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
            inference: serde_json::Value::Null,
            n_dets: 0,
            n_charts: 0,
            error_code: String::new(),
        }
    }
}
