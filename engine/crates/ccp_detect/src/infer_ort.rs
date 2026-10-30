//! Opt2 chart detection with explicit inference providers and bounded Auto recovery.

use std::path::{Path, PathBuf};
use std::sync::Mutex;

use crate::assemble::assemble_charts;
use crate::decode::{decode_obb, dets_from_raw, letterbox_rgb, preds_to_obb_rows};
use crate::geom::{
    cli_chart_from_detector, format_quad, DECODE_IOU, DEFAULT_CONF, DEFAULT_CROP, DEFAULT_MAX_DET,
    PRIMARY_SIZE, RECALL_SIZE,
};
use crate::handshake::{make_handshake, warmup_json};
use crate::paths::{default_deploy_dir, primary_onnx_path, recall_onnx_path};
use crate::{DetectResult, DetectSettings};

use crate::gpu_inference::{CachedInference, ExecutionRecord, InferenceSession};

static DETECT_PRIMARY: Mutex<Option<CachedInference>> = Mutex::new(None);
static DETECT_RECALL: Mutex<Option<CachedInference>> = Mutex::new(None);

pub fn inference_records(include_recall: bool) -> serde_json::Value {
    let detect=last_record(false);
    let recall=if include_recall {last_record(true)} else {None};
    serde_json::json!({"detect":detect,"recall":recall})
}
fn last_record(recall: bool) -> Option<ExecutionRecord> {
    let cache=if recall {&DETECT_RECALL} else {&DETECT_PRIMARY};
    cache.lock().ok().and_then(|c|c.as_ref().map(|active|active.record.clone()))
}

pub(crate) fn clear_sessions() {
    if let Ok(mut cache) = DETECT_PRIMARY.lock() { *cache = None; }
    if let Ok(mut cache) = DETECT_RECALL.lock() { *cache = None; }
}

pub fn last_detect_ep() -> Option<String> {
    last_detect_ep_for(false)
}

fn last_detect_ep_for(recall: bool) -> Option<String> {
    let cache = if recall {
        &DETECT_RECALL
    } else {
        &DETECT_PRIMARY
    };
    cache
        .lock()
        .ok()
        .and_then(|g| g.as_ref().map(|c| c.record.backend.clone()))
}

fn with_detect_session<F, T>(model_path: &Path, recall: bool, f: F) -> Result<T, String>
where F: FnMut(&mut InferenceSession, &str) -> Result<T, String>,
{
    let cache=if recall {&DETECT_RECALL} else {&DETECT_PRIMARY};
    let mut guard=cache.lock().map_err(|e|e.to_string())?;
    crate::gpu_inference::execute(&mut guard,model_path,f)
}

fn letterbox_to_nchw(canvas: &[u8], imgsz: i32) -> Vec<f32> {
    let size = imgsz.max(1) as usize;
    let mut chw = vec![0.0f32; 3 * size * size];
    for y in 0..size {
        for x in 0..size {
            let idx = (y * size + x) * 3;
            for c in 0..3 {
                chw[c * size * size + y * size + x] =
                    canvas.get(idx + c).copied().unwrap_or(114) as f32 / 255.0;
            }
        }
    }
    chw
}

#[allow(clippy::too_many_arguments)] // Keeps inference knobs explicit at the backend boundary.
fn run_detect_forward(
    model_path: &Path,
    rgb: &[f32],
    width: i32,
    height: i32,
    imgsz: i32,
    conf: f32,
    iou: f32,
    max_det: i32,
    recall: bool,
) -> Result<Vec<crate::geom::Det>, String> {
    let lb = letterbox_rgb(rgb, width, height, imgsz);
    let chw = letterbox_to_nchw(&lb.canvas, imgsz);
    with_detect_session(model_path, recall, |session, _ep| {
        let (dims, data) = session.forward(chw.clone(), imgsz as usize)?;
        validate_output(&dims, &data)?;
        let rows = preds_to_obb_rows(&data, &dims.iter().map(|&d| d as usize).collect::<Vec<_>>());
        let raw = decode_obb(&rows, conf, iou, max_det.max(1) as usize);
        Ok(dets_from_raw(&raw, imgsz, lb.gain, lb.pad_x, lb.pad_y))
    })
}

fn validate_output(dims: &[i64], data: &[f32]) -> Result<(), String> {
    let channels = i64::from(crate::geom::NUM_CLASSES) + 5;
    let valid = matches!(dims, [1, c, n] | [c, n] if *c == channels && *n > 0)
        || matches!(dims, [1, n, c] | [n, c] if *c == channels && *n > 0);
    let count = dims.iter().try_fold(1usize, |a, &d| {
        usize::try_from(d).ok().and_then(|v| a.checked_mul(v))
    });
    if !valid || count != Some(data.len()) || !data.iter().all(|v| v.is_finite()) {
        return Err(format!(
            "malformed detector output: shape {dims:?}, length {}",
            data.len()
        ));
    }
    Ok(())
}

fn resolve_deploy(settings: &DetectSettings) -> PathBuf {
    if settings.deploy_dir.as_os_str().is_empty() {
        default_deploy_dir()
    } else {
        settings.deploy_dir.clone()
    }
}

fn load_rgb_f32(path: &Path) -> Result<(Vec<f32>, i32, i32), String> {
    let img = ccp_core::sample::load_image(path).map_err(|e| e.to_string())?;
    Ok((img.rgb, img.width as i32, img.height as i32))
}

fn is_oom(err: &str) -> bool {
    let e = err.to_ascii_lowercase();
    e.contains("out of memory") || e.contains("oom")
}

pub fn warmup_detector_ort(_settings: &DetectSettings) -> DetectResult {
    let mut result = DetectResult {
        backend: "onnx".into(),
        runtime: "ort".into(),
        recall_source: "none".into(),
        ..DetectResult::default()
    };
    let deploy = resolve_deploy(_settings);
    let primary = primary_onnx_path(&deploy);
    if !primary.is_file() {
        result.error = format!("missing {}", primary.display());
        return result;
    }
    let imgsz = PRIMARY_SIZE;
    let chw = vec![0.0f32; (3 * imgsz * imgsz) as usize];
    match with_detect_session(&primary, false, |session, ep| {
            let (dims,data)=session.forward(chw.clone(),imgsz as usize)?;
            validate_output(&dims,&data)?;
            Ok(ep.to_string())
        }) {
            Ok(ep) => {
                result.ok = true;
                result.device = ep;
                if result.device.contains(":wgpu:") { result.runtime="burn-wgpu".into(); }
                result.json = warmup_json(&result.device);
                result.inference = inference_records(false);
                result
            }
            Err(err) if is_oom(&err) => {
                result.error = format!("failed: oom_on_warmup ({err})");
                result
            }
            Err(err) => {
                result.error = err;
                result
            }
    }
}

pub fn detect_chart_ort(image_path: &Path, settings: &DetectSettings) -> DetectResult {
    let mut result = DetectResult {
        backend: "onnx".into(),
        runtime: "ort".into(),
        recall_source: "none".into(),
        ..DetectResult::default()
    };
    let (rgb, width, height) = match load_rgb_f32(image_path) {
        Ok(v) => v,
        Err(err) => {
            result.error = err;
            return result;
        }
    };
    let deploy = resolve_deploy(settings);
    let primary = primary_onnx_path(&deploy);
    if !primary.is_file() {
        result.error = format!("missing {}", primary.display());
        return result;
    }
    let conf = if settings.conf > 0.0 {
        settings.conf
    } else {
        DEFAULT_CONF
    };
    let iou = if settings.iou > 0.0 {
        settings.iou
    } else {
        DECODE_IOU
    };
    let max_det = if settings.max_det > 0 {
        settings.max_det
    } else {
        DEFAULT_MAX_DET
    };
    let crop = if settings.crop > 0.0 {
        settings.crop
    } else {
        DEFAULT_CROP
    };

    let mut dets = match run_detect_forward(
        &primary,
        &rgb,
        width,
        height,
        PRIMARY_SIZE,
        conf,
        iou,
        max_det,
        false,
    ) {
        Ok(d) => d,
        Err(err) => {
            result.error = err;
            return result;
        }
    };
    result.device = last_detect_ep_for(false).unwrap_or_else(|| "unknown".into());
    if result.device.contains(":wgpu:") { result.runtime="burn-wgpu".into(); }
    result.recall_source = "primary".into();
    let mut charts = assemble_charts(&dets, &settings.prefer, crop, width, height);
    if charts.is_empty() && settings.allow_recall {
        let recall_path = recall_onnx_path(&deploy);
        if recall_path.is_file() {
            match run_detect_forward(
                &recall_path,
                &rgb,
                width,
                height,
                RECALL_SIZE,
                conf.min(0.05),
                iou,
                max_det,
                true,
            ) {
                Ok(recall_dets) => {
                    if let Some(recall_ep) = last_detect_ep_for(true) {
                        if recall_ep != result.device {
                            result.device = format!("mixed:{}+{}", result.device, recall_ep);
                        }
                    }
                    dets = recall_dets;
                    result.recall_source = "recall_1536".into();
                    charts = assemble_charts(&dets, &settings.prefer, crop, width, height);
                }
                Err(err) => return failed_recall(result,err),
            }
        }
    }
    result.inference = inference_records(result.recall_source == "recall_1536");
    result.n_dets = dets.len() as i32;
    if charts.is_empty() {
        result.error = "no lattice".into();
        result.n_charts = 0;
        return result;
    }
    result.ok = true;
    result.n_charts = charts.len() as i32;
    result.detection = charts[0].clone();
    result.chart_type = charts[0].chart_type.clone();
    result.cli_chart = cli_chart_from_detector(&charts[0].chart_type).to_string();
    result.quad = format_quad(charts[0].corners);
    result.charts = charts.clone();
    result.inference = inference_records(result.recall_source == "recall_1536");
    match make_handshake(&result, &charts, crop, image_path) {
        Ok(json) => result.json = json,
        Err(err) => result.error = err.to_string(),
    }
    result
}

/// Recall only runs after primary produced no usable chart. An unavailable
/// recall engine is not evidence that the photograph contains no chart.
fn failed_recall(mut result: DetectResult, reason: String) -> DetectResult {
    result.ok=false;
    result.error=format!("Recall chart detection failed: {reason}. Check setup, choose Auto or CPU, and retry detection.");
    result.recall_source="failed".into();
    result.inference=inference_records(false);
    result.inference["recall_error"]=serde_json::json!(reason);
    result
}

#[cfg(test)]
mod recall_failure_tests {
    #[test]
    fn failed_recall_is_not_reported_as_a_no_chart_result() {
        let result=super::failed_recall(crate::DetectResult::default(),"CPU execution failed after GPU was unavailable".into());
        assert!(!result.ok);
        assert!(result.error.contains("Recall chart detection failed"));
        assert!(result.error.contains("CPU execution failed"));
        assert_ne!(result.error,"no lattice");
        assert_eq!(result.recall_source,"failed");
        assert_eq!(result.inference["recall_error"],"CPU execution failed after GPU was unavailable");
    }
}
