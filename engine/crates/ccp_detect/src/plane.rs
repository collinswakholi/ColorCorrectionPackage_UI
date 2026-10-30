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
    Some(Rect {x:nums[0],y:nums[1],width:nums[2].checked_sub(nums[0])?,height:nums[3].checked_sub(nums[1])?})
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
/// Shared bilinear preprocessing for production inference and isolated benchmarks.
pub fn resize_rgb_to_nchw(rgb: &[u8], width: u32, height: u32, size: i32) -> Vec<f32> {
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
    if channels < 5 || num_preds == 0 || channels.checked_mul(num_preds) != Some(data.len()) {
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

/// Decode and select the same largest/shrunken plane used by production.
#[cfg(feature = "ort-plane")]
pub fn plane_box_from_output(data: &[f32], dims: &[i64], width: i32, height: i32) -> Option<Rect> {
    select_plane_box(&parse_yolo_output(data, dims, width, height), PLANE_SHRINK)
}

#[cfg(feature = "ort-plane")]
use crate::gpu_inference::CachedInference;

#[cfg(feature = "ort-plane")]
static PLANE_SESSION: std::sync::Mutex<Option<CachedInference>> = std::sync::Mutex::new(None);

#[cfg(feature = "ort-plane")]
pub(crate) fn clear_session() {
    if let Ok(mut cache) = PLANE_SESSION.lock() { *cache = None; }
}

pub fn last_plane_inference() -> Option<serde_json::Value> {
    #[cfg(feature="ort-plane")]
    if last_plane_ep().is_some() {
        return PLANE_SESSION.lock().ok().and_then(|cache|cache.as_ref().and_then(|c|serde_json::to_value(&c.record).ok()));
    }
    None
}

static PLANE_EXECUTION: std::sync::Mutex<Option<String>> = std::sync::Mutex::new(None);

pub fn reset_plane_execution() {
    if let Ok(mut used) = PLANE_EXECUTION.lock() { *used = None; }
}

/// Provider that actually completed inference in the current processing request.
pub fn last_plane_ep() -> Option<String> {
    PLANE_EXECUTION.lock().ok().and_then(|used| used.clone())
}

#[cfg(feature = "ort-plane")]
fn detect_plane_ort(
    rgb: &[u8],
    width: u32,
    height: u32,
    model_path: &std::path::Path,
) -> Result<Option<Rect>, PlaneError> {
    let chw = resize_rgb_to_nchw(rgb, width, height, PLANE_INPUT);
    let mut guard=PLANE_SESSION.lock().map_err(|e|PlaneError::Message(e.to_string()))?;
    let (dims,data,backend)=crate::gpu_inference::execute(&mut guard,model_path,|session,backend| {
        let (dims,data)=session.forward(chw.clone(),PLANE_INPUT as usize)?;
        validate_output(&dims,&data).map_err(|e|e.to_string())?;
        Ok((dims,data,backend.to_string()))
    }).map_err(PlaneError::Message)?;
    if let Ok(mut used) = PLANE_EXECUTION.lock() { *used = Some(backend); }
    Ok(plane_box_from_output(
        &data,
        &dims,
        width as i32,
        height as i32,
    ))
}

#[cfg(feature = "ort-plane")]
fn validate_output(dims: &[i64], data: &[f32]) -> Result<(), PlaneError> {
    let valid = matches!(dims, [1, c, n] | [c, n] if (5..=64).contains(c) && *n > 0)
        || matches!(dims, [1, n, c] | [n, c] if (5..=64).contains(c) && *n > 0);
    let count = dims.iter().try_fold(1usize, |a, &d| {
        usize::try_from(d).ok().and_then(|v| a.checked_mul(v))
    });
    if !valid || count != Some(data.len()) || !data.iter().all(|v| v.is_finite()) {
        return Err(PlaneError::Message(format!(
            "malformed plane output: shape {dims:?}, length {}",
            data.len()
        )));
    }
    Ok(())
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

#[cfg(test)]
mod crop_validation_tests {
    #[test]
    fn extreme_manual_crop_does_not_overflow() {
        assert!(super::parse_crop_rect("-2147483648,0,2147483647,1").is_none());
        let crop=super::parse_crop_rect("2,3,12,13").unwrap();
        assert_eq!((crop.x,crop.y,crop.width,crop.height),(2,3,10,10));
    }
}
