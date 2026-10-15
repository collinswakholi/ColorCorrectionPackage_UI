//! Opt2 chart detect via ONNX Runtime (DirectML / CUDA / CPU EP).

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
use crate::probe::{runtime_assignment, runtime_policy, DevicePolicy};
use crate::{DetectResult, DetectSettings};

#[derive(Clone, Copy, PartialEq, Eq)]
enum DetectEp {
    Cuda,
    DirectMl,
    OpenVino,
    CoreMl,
    Cpu,
}

impl DetectEp {
    fn key(self) -> &'static str {
        match self {
            Self::Cuda => "cuda",
            Self::DirectMl => "directml",
            Self::OpenVino => "openvino",
            Self::CoreMl => "coreml",
            Self::Cpu => "cpu",
        }
    }
}

fn detect_ep_order() -> Vec<DetectEp> {
    let policy = runtime_policy();
    let want = runtime_assignment().detect;
    let mut order = match policy {
        DevicePolicy::Cpu => vec![DetectEp::Cpu],
        DevicePolicy::Cuda => vec![DetectEp::Cuda, DetectEp::DirectMl, DetectEp::Cpu],
        DevicePolicy::DirectMl => vec![DetectEp::DirectMl, DetectEp::Cpu],
        DevicePolicy::OpenVino => vec![DetectEp::OpenVino, DetectEp::Cpu],
        DevicePolicy::Mlx => vec![DetectEp::CoreMl, DetectEp::Cpu],
        DevicePolicy::Auto => {
            let order = vec![
                DetectEp::Cuda,
                DetectEp::OpenVino,
                DetectEp::DirectMl,
                DetectEp::Cpu,
            ];
            #[cfg(target_os = "macos")]
            order.insert(order.len() - 1, DetectEp::CoreMl);
            order
        }
    };
    if let Some(pref) = match want.as_str() {
        "cuda" => Some(DetectEp::Cuda),
        "directml" => Some(DetectEp::DirectMl),
        "openvino" => Some(DetectEp::OpenVino),
        "coreml" => Some(DetectEp::CoreMl),
        "cpu" => Some(DetectEp::Cpu),
        _ => None,
    } {
        order.retain(|e| *e != pref);
        order.insert(0, pref);
    }
    let mut seen = Vec::new();
    order.retain(|e| {
        if seen.contains(e) {
            false
        } else {
            seen.push(*e);
            true
        }
    });
    order
}

fn commit_detect_session(model_path: &Path, ep: DetectEp) -> Result<ort::session::Session, String> {
    use ort::ep::ExecutionProvider;
    use ort::session::Session;

    let mut builder = Session::builder().map_err(|e| e.to_string())?;
    let threads = runtime_assignment().threads.max(1) as usize;
    builder = builder
        .with_intra_threads(threads)
        .map_err(|e| e.to_string())?;
    match ep {
        DetectEp::Cpu => {}
        DetectEp::Cuda => {
            ort::ep::CUDA::default()
                .register(&mut builder)
                .map_err(|e| e.to_string())?;
        }
        DetectEp::DirectMl => {
            ort::ep::DirectML::default()
                .register(&mut builder)
                .map_err(|e| e.to_string())?;
        }
        DetectEp::OpenVino => {
            ort::ep::OpenVINO::default()
                .register(&mut builder)
                .map_err(|e| e.to_string())?;
        }
        DetectEp::CoreMl => {
            ort::ep::CoreML::default()
                .register(&mut builder)
                .map_err(|e| e.to_string())?;
        }
    }
    builder
        .commit_from_file(model_path)
        .map_err(|e| e.to_string())
}

struct CachedDetect {
    path: PathBuf,
    policy: String,
    ep: String,
    session: ort::session::Session,
}

static DETECT_PRIMARY: Mutex<Option<CachedDetect>> = Mutex::new(None);
static DETECT_RECALL: Mutex<Option<CachedDetect>> = Mutex::new(None);

pub fn last_detect_ep() -> Option<String> {
    DETECT_PRIMARY
        .lock()
        .ok()
        .and_then(|g| g.as_ref().map(|c| c.ep.clone()))
}

fn with_detect_session<F, T>(model_path: &Path, recall: bool, f: F) -> Result<T, String>
where
    F: FnOnce(&mut ort::session::Session, &str) -> Result<T, String>,
{
    let policy = runtime_policy().as_str().to_string();
    let lock = if recall {
        DETECT_RECALL.lock().map_err(|e| e.to_string())?
    } else {
        DETECT_PRIMARY.lock().map_err(|e| e.to_string())?
    };
    let mut guard = lock;
    let reuse = guard
        .as_ref()
        .is_some_and(|c| c.path == model_path && c.policy == policy);
    if !reuse {
        let mut last_err = "no detect execution provider".to_string();
        let mut built: Option<CachedDetect> = None;
        for ep in detect_ep_order() {
            match commit_detect_session(model_path, ep) {
                Ok(session) => {
                    if ep != DetectEp::Cpu {
                        eprintln!("ccp_detect: detect ONNX on {}", ep.key());
                    } else if last_err != "no detect execution provider" {
                        eprintln!("ccp_detect: detect ONNX fell back to CPU ({last_err})");
                    }
                    built = Some(CachedDetect {
                        path: model_path.to_path_buf(),
                        policy: policy.clone(),
                        ep: ep.key().into(),
                        session,
                    });
                    break;
                }
                Err(err) => {
                    if ep != DetectEp::Cpu {
                        eprintln!("ccp_detect: detect {} unavailable: {err}", ep.key());
                    }
                    last_err = err;
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
    use ort::value::Tensor;

    let lb = letterbox_rgb(rgb, width, height, imgsz);
    let chw = letterbox_to_nchw(&lb.canvas, imgsz);
    let input = Tensor::from_array(([1i64, 3, i64::from(imgsz), i64::from(imgsz)], chw))
        .map_err(|e| e.to_string())?;
    with_detect_session(model_path, recall, |session, _ep| {
        let outputs = session
            .run(ort::inputs![input])
            .map_err(|e| e.to_string())?;
        let (dims, data) = extract_first_f32(&outputs)?;
        let rows = preds_to_obb_rows(&data, &dims.iter().map(|&d| d as usize).collect::<Vec<_>>());
        let raw = decode_obb(&rows, conf, iou, max_det.max(1) as usize);
        Ok(dets_from_raw(&raw, imgsz, lb.gain, lb.pad_x, lb.pad_y))
    })
}

fn extract_first_f32(
    outputs: &ort::session::SessionOutputs<'_>,
) -> Result<(Vec<i64>, Vec<f32>), String> {
    let (_name, value) = outputs
        .iter()
        .next()
        .ok_or_else(|| "empty detect output".to_string())?;
    let (shape, data) = value
        .try_extract_tensor::<f32>()
        .map_err(|e| e.to_string())?;
    Ok((shape.iter().copied().collect(), data.to_vec()))
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
    use ort::value::Tensor;
    let input = Tensor::from_array(([1i64, 3, i64::from(imgsz), i64::from(imgsz)], chw))
        .map_err(|e| e.to_string());
    match input {
        Ok(tensor) => match with_detect_session(&primary, false, |session, ep| {
            session
                .run(ort::inputs![tensor])
                .map_err(|e| e.to_string())?;
            Ok(ep.to_string())
        }) {
            Ok(ep) => {
                result.ok = true;
                result.device = ep;
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
        },
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

    result.device = last_detect_ep().unwrap_or_else(|| "cpu".into());
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
