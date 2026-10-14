//! Plane (white-card) crop: 512 / conf 0.7 / NMS 0.6 / shrink 0.95 / largest box.
//! ONNX inference is compiled only with `--features ort-plane`.

use serde::{Deserialize, Serialize};

pub const PLANE_INPUT: i32 = 512;
pub const PLANE_CONF: f32 = 0.7;
pub const PLANE_NMS: f32 = 0.6;
pub const PLANE_SHRINK: f32 = 0.95;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl Rect {
    pub fn from_xyxy(x1: i32, y1: i32, x2: i32, y2: i32) -> Self {
        Self {
            x: x1,
            y: y1,
            width: x2 - x1,
            height: y2 - y1,
        }
    }

    pub fn full(width: i32, height: i32) -> Self {
        Self {
            x: 0,
            y: 0,
            width,
            height,
        }
    }

    pub fn x2(self) -> i32 {
        self.x + self.width
    }

    pub fn y2(self) -> i32 {
        self.y + self.height
    }

    pub fn area(self) -> i64 {
        i64::from(self.width.max(0)) * i64::from(self.height.max(0))
    }

    pub fn shrink(self, ratio: f32) -> Self {
        let shrink = 1.0 - ratio;
        let dx = (self.width as f32 * shrink) as i32;
        let dy = (self.height as f32 * shrink) as i32;
        Self {
            x: self.x + dx,
            y: self.y + dy,
            width: self.width - 2 * dx,
            height: self.height - 2 * dy,
        }
    }
}

/// `"x1,y1,x2,y2"` → `Rect(x1, y1, x2-x1, y2-y1)`.
pub fn parse_crop_rect(s: &str) -> Option<Rect> {
    let parts: Vec<&str> = s
        .split(',')
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .collect();
    if parts.len() != 4 {
        return None;
    }
    let nums: Result<Vec<i32>, _> = parts.iter().map(|p| p.parse::<i32>()).collect();
    let nums = nums.ok()?;
    Some(Rect::from_xyxy(nums[0], nums[1], nums[2], nums[3]))
}

/// Manual crop wins; else detected box; miss → full frame.
pub fn plane_or_full(
    frame_w: i32,
    frame_h: i32,
    crop_rect: Option<&str>,
    detected: Option<Rect>,
) -> Rect {
    if let Some(s) = crop_rect {
        if let Some(r) = parse_crop_rect(s) {
            return r;
        }
    }
    detected.unwrap_or_else(|| Rect::full(frame_w, frame_h))
}

#[derive(Debug, thiserror::Error)]
pub enum PlaneError {
    #[error("ccp_detect was built without ONNX Runtime. Enable the `ort-plane` Cargo feature.")]
    OrtDisabled,
    #[error("plane detect failed: {0}")]
    Message(String),
}

#[derive(Debug, Clone)]
pub struct PlaneDet {
    pub rect: Rect,
    pub confidence: f32,
    pub class_id: i32,
}

fn aabb_iou(a: Rect, b: Rect) -> f32 {
    let ix0 = a.x.max(b.x);
    let iy0 = a.y.max(b.y);
    let ix1 = a.x2().min(b.x2());
    let iy1 = a.y2().min(b.y2());
    let iw = (ix1 - ix0).max(0);
    let ih = (iy1 - iy0).max(0);
    let inter = (iw as f32) * (ih as f32);
    let area_a = a.area() as f32;
    let area_b = b.area() as f32;
    let denom = area_a + area_b - inter;
    if denom <= 1e-6 {
        0.0
    } else {
        inter / denom
    }
}

pub fn nms_boxes(mut dets: Vec<PlaneDet>, iou_thr: f32) -> Vec<PlaneDet> {
    dets.sort_by(|a, b| {
        b.confidence
            .partial_cmp(&a.confidence)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    let mut kept = Vec::new();
    for det in dets {
        if kept
            .iter()
            .any(|p: &PlaneDet| aabb_iou(det.rect, p.rect) > iou_thr)
        {
            continue;
        }
        kept.push(det);
    }
    kept
}

pub fn largest_box(dets: &[PlaneDet]) -> Option<PlaneDet> {
    dets.iter().max_by_key(|d| d.area_key()).cloned()
}

impl PlaneDet {
    fn area_key(&self) -> i64 {
        self.rect.area()
    }
}

/// Apply shrink 0.95 to the largest detection (CCP YOLO plane).
pub fn select_plane_box(dets: &[PlaneDet], shrink: f32) -> Option<Rect> {
    largest_box(dets).map(|d| d.rect.shrink(shrink))
}

#[cfg(not(feature = "ort-plane"))]
pub fn detect_plane(
    _rgb: &[u8],
    _width: u32,
    _height: u32,
    _model_path: &std::path::Path,
) -> Result<Option<Rect>, PlaneError> {
    Err(PlaneError::OrtDisabled)
}

#[cfg(feature = "ort-plane")]
pub fn detect_plane(
    rgb: &[u8],
    width: u32,
    height: u32,
    model_path: &std::path::Path,
) -> Result<Option<Rect>, PlaneError> {
    detect_plane_ort(rgb, width, height, model_path)
}

#[cfg(feature = "ort-plane")]
fn resize_rgb_to_nchw(rgb: &[u8], width: u32, height: u32, size: i32) -> Vec<f32> {
    let size = size.max(1) as u32;
    let mut chw = vec![0.0f32; (3 * size * size) as usize];
    for y in 0..size {
        let sy = (y as f32 + 0.5) * height as f32 / size as f32 - 0.5;
        let y0 = (sy.floor() as i32).clamp(0, height as i32 - 1) as u32;
        let y1 = (y0 + 1).min(height - 1);
        let fy = sy - y0 as f32;
        for x in 0..size {
            let sx = (x as f32 + 0.5) * width as f32 / size as f32 - 0.5;
            let x0 = (sx.floor() as i32).clamp(0, width as i32 - 1) as u32;
            let x1 = (x0 + 1).min(width - 1);
            let fx = sx - x0 as f32;
            for c in 0..3 {
                let i00 = ((y0 * width + x0) * 3 + c) as usize;
                let i10 = ((y0 * width + x1) * 3 + c) as usize;
                let i01 = ((y1 * width + x0) * 3 + c) as usize;
                let i11 = ((y1 * width + x1) * 3 + c) as usize;
                let v00 = rgb.get(i00).copied().unwrap_or(0) as f32;
                let v10 = rgb.get(i10).copied().unwrap_or(0) as f32;
                let v01 = rgb.get(i01).copied().unwrap_or(0) as f32;
                let v11 = rgb.get(i11).copied().unwrap_or(0) as f32;
                let v =
                    (1.0 - fy) * ((1.0 - fx) * v00 + fx * v10) + fy * ((1.0 - fx) * v01 + fx * v11);
                chw[(c * size * size + y * size + x) as usize] = v / 255.0;
            }
        }
    }
    chw
}

#[cfg(feature = "ort-plane")]
fn parse_yolo_output(data: &[f32], dims: &[i64], img_w: i32, img_h: i32) -> Vec<PlaneDet> {
    // YOLOv8: (1, 4+C, N) or (1, N, 4+C)
    let (channels, num_preds, chan_major) = match dims {
        [1, c, n] if *c <= 64 => (*c as usize, *n as usize, true),
        [1, n, c] if *c <= 64 => (*c as usize, *n as usize, false),
        [c, n] if *c <= 64 => (*c as usize, *n as usize, true),
        [n, c] if *c <= 64 => (*c as usize, *n as usize, false),
        _ => return Vec::new(),
    };
    if channels < 5 || num_preds == 0 {
        return Vec::new();
    }
    let num_classes = channels - 4;
    let x_scale = img_w as f32 / PLANE_INPUT as f32;
    let y_scale = img_h as f32 / PLANE_INPUT as f32;
    let at = |ch: usize, p: usize| -> f32 {
        if chan_major {
            data[ch * num_preds + p]
        } else {
            data[p * channels + ch]
        }
    };
    let mut dets = Vec::new();
    for p in 0..num_preds {
        let mut max_score = 0.0f32;
        let mut max_class = 0i32;
        for c in 0..num_classes {
            let score = at(4 + c, p);
            if score > max_score {
                max_score = score;
                max_class = c as i32;
            }
        }
        if max_score < PLANE_CONF {
            continue;
        }
        let cx = at(0, p);
        let cy = at(1, p);
        let w = at(2, p);
        let h = at(3, p);
        let x1 = ((cx - w / 2.0) * x_scale) as i32;
        let y1 = ((cy - h / 2.0) * y_scale) as i32;
        let x2 = ((cx + w / 2.0) * x_scale) as i32;
        let y2 = ((cy + h / 2.0) * y_scale) as i32;
        let x1 = x1.clamp(0, img_w);
        let y1 = y1.clamp(0, img_h);
        let x2 = x2.clamp(0, img_w);
        let y2 = y2.clamp(0, img_h);
        if x2 > x1 && y2 > y1 {
            dets.push(PlaneDet {
                rect: Rect::from_xyxy(x1, y1, x2, y2),
                confidence: max_score,
                class_id: max_class,
            });
        }
    }
    nms_boxes(dets, PLANE_NMS)
}

#[cfg(feature = "ort-plane")]
#[derive(Clone, Copy, PartialEq, Eq)]
enum PlaneEp {
    Cuda,
    DirectMl,
    OpenVino,
    CoreMl,
    Cpu,
}

#[cfg(feature = "ort-plane")]
impl PlaneEp {
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

#[cfg(feature = "ort-plane")]
fn plane_ep_order() -> Vec<PlaneEp> {
    use crate::probe::{runtime_assignment, runtime_policy, DevicePolicy};
    let policy = runtime_policy();
    let want = runtime_assignment().plane;
    let mut order = match policy {
        DevicePolicy::Cpu => vec![PlaneEp::Cpu],
        DevicePolicy::Cuda => vec![PlaneEp::Cuda, PlaneEp::DirectMl, PlaneEp::Cpu],
        DevicePolicy::DirectMl => vec![PlaneEp::DirectMl, PlaneEp::Cpu],
        DevicePolicy::OpenVino => vec![PlaneEp::OpenVino, PlaneEp::Cpu],
        DevicePolicy::Mlx => vec![PlaneEp::CoreMl, PlaneEp::Cpu],
        DevicePolicy::Auto => vec![
            PlaneEp::Cuda,
            PlaneEp::OpenVino,
            PlaneEp::DirectMl,
            PlaneEp::CoreMl,
            PlaneEp::Cpu,
        ],
    };
    if let Some(pref) = match want.as_str() {
        "cuda" => Some(PlaneEp::Cuda),
        "directml" => Some(PlaneEp::DirectMl),
        "openvino" => Some(PlaneEp::OpenVino),
        "coreml" => Some(PlaneEp::CoreMl),
        "cpu" => Some(PlaneEp::Cpu),
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

#[cfg(feature = "ort-plane")]
fn commit_plane_session(
    model_path: &std::path::Path,
    ep: PlaneEp,
) -> Result<ort::session::Session, String> {
    use ort::ep::ExecutionProvider;
    use ort::session::Session;

    let mut builder = Session::builder().map_err(|e| e.to_string())?;
    let threads = crate::probe::runtime_assignment().threads.max(1) as usize;
    builder = builder
        .with_intra_threads(threads)
        .map_err(|e| e.to_string())?;
    match ep {
        PlaneEp::Cpu => {}
        PlaneEp::Cuda => {
            ort::ep::CUDA::default()
                .register(&mut builder)
                .map_err(|e| e.to_string())?;
        }
        PlaneEp::DirectMl => {
            ort::ep::DirectML::default()
                .register(&mut builder)
                .map_err(|e| e.to_string())?;
        }
        PlaneEp::OpenVino => {
            ort::ep::OpenVINO::default()
                .register(&mut builder)
                .map_err(|e| e.to_string())?;
        }
        PlaneEp::CoreMl => {
            ort::ep::CoreML::default()
                .register(&mut builder)
                .map_err(|e| e.to_string())?;
        }
    }
    builder
        .commit_from_file(model_path)
        .map_err(|e| e.to_string())
}

#[cfg(feature = "ort-plane")]
struct CachedPlane {
    path: std::path::PathBuf,
    policy: String,
    ep: String,
    session: ort::session::Session,
}

#[cfg(feature = "ort-plane")]
static PLANE_SESSION: std::sync::Mutex<Option<CachedPlane>> = std::sync::Mutex::new(None);

/// Execution provider last used for plane crop (`cuda`, `directml`, `openvino`, `coreml`, `cpu`).
pub fn last_plane_ep() -> Option<String> {
    #[cfg(feature = "ort-plane")]
    {
        PLANE_SESSION
            .lock()
            .ok()
            .and_then(|g| g.as_ref().map(|c| c.ep.clone()))
    }
    #[cfg(not(feature = "ort-plane"))]
    {
        None
    }
}

#[cfg(feature = "ort-plane")]
fn plane_session_mut(
    model_path: &std::path::Path,
) -> Result<std::sync::MutexGuard<'static, Option<CachedPlane>>, PlaneError> {
    use crate::probe::runtime_policy;
    let policy = runtime_policy().as_str().to_string();
    let mut guard = PLANE_SESSION
        .lock()
        .map_err(|e| PlaneError::Message(e.to_string()))?;
    let reuse = guard
        .as_ref()
        .is_some_and(|c| c.path == model_path && c.policy == policy);
    if reuse {
        return Ok(guard);
    }
    let mut last_err = "no plane execution provider".to_string();
    for ep in plane_ep_order() {
        match commit_plane_session(model_path, ep) {
            Ok(session) => {
                if ep != PlaneEp::Cpu {
                    eprintln!("ccp_detect: plane ONNX on {}", ep.key());
                } else if last_err != "no plane execution provider" {
                    eprintln!("ccp_detect: plane ONNX fell back to CPU ({last_err})");
                }
                *guard = Some(CachedPlane {
                    path: model_path.to_path_buf(),
                    policy,
                    ep: ep.key().into(),
                    session,
                });
                return Ok(guard);
            }
            Err(err) => {
                if ep != PlaneEp::Cpu {
                    eprintln!("ccp_detect: plane {} unavailable: {err}", ep.key());
                }
                last_err = err;
            }
        }
    }
    Err(PlaneError::Message(last_err))
}

#[cfg(feature = "ort-plane")]
fn detect_plane_ort(
    rgb: &[u8],
    width: u32,
    height: u32,
    model_path: &std::path::Path,
) -> Result<Option<Rect>, PlaneError> {
    use ort::value::Tensor;

    let chw = resize_rgb_to_nchw(rgb, width, height, PLANE_INPUT);
    let input = Tensor::from_array((
        [1i64, 3, i64::from(PLANE_INPUT), i64::from(PLANE_INPUT)],
        chw,
    ))
    .map_err(|e| PlaneError::Message(e.to_string()))?;
    let mut guard = plane_session_mut(model_path)?;
    let session = guard
        .as_mut()
        .ok_or_else(|| PlaneError::Message("plane session missing".into()))?;
    let outputs = session
        .session
        .run(ort::inputs![input])
        .map_err(|e| PlaneError::Message(e.to_string()))?;
    let (dims, data) = extract_first_f32(&outputs)?;
    let dets = parse_yolo_output(&data, &dims, width as i32, height as i32);
    Ok(select_plane_box(&dets, PLANE_SHRINK))
}

#[cfg(feature = "ort-plane")]
fn extract_first_f32(
    outputs: &ort::session::SessionOutputs<'_>,
) -> Result<(Vec<i64>, Vec<f32>), PlaneError> {
    let (_name, value) = outputs
        .iter()
        .next()
        .ok_or_else(|| PlaneError::Message("empty plane output".into()))?;
    let (shape, data) = value
        .try_extract_tensor::<f32>()
        .map_err(|e| PlaneError::Message(e.to_string()))?;
    Ok((shape.iter().copied().collect(), data.to_vec()))
}

/// Detect the plane or fall back to the full frame (FFC miss path).
pub fn detect_plane_or_full(
    rgb: &[u8],
    width: u32,
    height: u32,
    crop_rect: Option<&str>,
    model_path: Option<&std::path::Path>,
) -> Rect {
    if let Some(s) = crop_rect {
        if let Some(r) = parse_crop_rect(s) {
            return r;
        }
    }
    if let Some(path) = model_path {
        if let Ok(Some(r)) = detect_plane(rgb, width, height, path) {
            return r;
        }
    }
    Rect::full(width as i32, height as i32)
}
