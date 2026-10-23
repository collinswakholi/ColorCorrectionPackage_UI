//! Opt2 chart detect via ONNX Runtime (DirectML / WebGPU EP, CPU fallback disabled).

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

use crate::gpu_inference::{provider_order as detect_ep_order, commit_inference_session as commit_detect_session, InferenceSession};

struct CachedDetect {
    model_identity: String,
    policy: String,
    ep: String,
    session: InferenceSession,
}

static DETECT_PRIMARY: Mutex<Option<CachedDetect>> = Mutex::new(None);
static DETECT_RECALL: Mutex<Option<CachedDetect>> = Mutex::new(None);

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
        .and_then(|g| g.as_ref().map(|c| c.ep.clone()))
}

fn with_detect_session<F, T>(model_path: &Path, recall: bool, f: F) -> Result<T, String>
where
    F: FnOnce(&mut InferenceSession, &str) -> Result<T, String>,
{
    let policy = crate::cache_key::runtime_cache_identity();
    let model_identity = crate::cache_key::model_cache_identity(model_path);
    let lock = if recall {
        DETECT_RECALL.lock().map_err(|e| e.to_string())?
    } else {
        DETECT_PRIMARY.lock().map_err(|e| e.to_string())?
    };
    let mut guard = lock;
    let reuse = guard
        .as_ref()
        .is_some_and(|c| c.model_identity == model_identity && c.policy == policy);
    if !reuse {
        let mut last_err = "no detect execution provider".to_string();
        let mut built: Option<CachedDetect> = None;
        for ep in detect_ep_order() {
            match commit_detect_session(model_path, ep) {
                Ok(session) => {
                    eprintln!("ccp_detect: detect ONNX on {}", ep.key());
                    built = Some(CachedDetect {
                        model_identity: model_identity.clone(),
                        policy: policy.clone(),
                        ep: session.backend(ep),
                        session,
                    });
                    break;
                }
                Err(err) => {
                    eprintln!("ccp_detect: detect {} unavailable: {err}", ep.key());
                    last_err.push_str(&format!("; {}: {err}", ep.key()));
                }
            }
        }
        *guard = Some(built.ok_or(last_err)?);
    }
    let cached = guard
        .as_mut()
        .ok_or_else(|| "detect session missing".to_string())?;
    f(&mut cached.session, &cached.ep)
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
        let (dims, data) = session.forward(chw, imgsz as usize)?;
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
            let (dims,data)=session.forward(chw,imgsz as usize)?;
            validate_output(&dims,&data)?;
            Ok(ep.to_string())
        }) {
            Ok(ep) => {
                result.ok = true;
                result.device = ep;
                if result.device.contains(":wgpu:") { result.runtime="burn-wgpu".into(); }
                result.json = warmup_json(&result.device);
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
                Err(err) => {
                    eprintln!("ccp_detect: recall ONNX failed: {err}");
                }
            }
        }
    }
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
    match make_handshake(&result, &charts, crop, image_path) {
        Ok(json) => result.json = json,
        Err(err) => result.error = err.to_string(),
    }
    result
}
