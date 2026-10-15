//! Wire `ccp_core` stages to an Opt2/manual handshake. One f32 working buffer.

use std::collections::HashMap;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::JoinHandle;
use std::time::{Instant, SystemTime};

use rayon::prelude::*;

use ccp_core::color::RgbColorSpace;
use ccp_core::ffc::{
    apply_ffc_gc_fused_inplace, apply_ffc_inplace, apply_ffc_pixel, compute_multiplier,
    sample_gain_at, FfcFitMethod, FfcFitParams, Upsample,
};
use ccp_core::honest::{cross_validate_correction, delta_e00_in_space};
use ccp_core::lut::{write_cube, LutPipeline, DEFAULT_LUT_SIZE};
use ccp_core::maps::{
    apply_correction_pixel, build_encoded_lut, fit_correction_model, parse_model_kind,
    CorrectionModel, CorrectionSettings, MappingOptions, ModelKind, APPLY_LUT_MIN_PIXELS,
    APPLY_LUT_SIZE,
};
use ccp_core::phase2::emit_rpcc2_teacher;
use ccp_core::refs::{
    chart_spec_from_name, load_neutrals_table, ChartPatch, ChartSpec, Quad, Vec2,
};
use ccp_core::sample::{
    load_image, sample_patch_obbs_view, sample_patch_obbs_view_transform, RgbImage, RgbView,
    SamplingOptions,
};
use ccp_core::stages::{
    apply_cc_inplace, apply_encoded_lut_inplace, apply_gc_inplace, apply_gc_pixel,
    apply_wb_inplace, apply_wb_pixel, extrapolate_saturated, fit_gc, fit_wb, GammaModel,
    StageConfig, WhiteBalance,
};
use ccp_core::{parse_rgb_color_space, parse_white_point, RIDGE_LAMBDA};
use ccp_detect::plane::detect_plane_or_full;
use ccp_detect::{
    apply_encoded_lut_gpu, apply_encoded_lut_ort, runtime_assignment, runtime_policy, DevicePolicy,
};
use image::codecs::png::{CompressionType, FilterType, PngEncoder};
use image::{ExtendedColorType, ImageEncoder, RgbImage as Rgb8};
use jpeg_encoder::{ColorType as JpegColorType, Encoder as JpegEncoder, SamplingFactor};
use ndarray::{Array2, Array3};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

const PREVIEW_MAX_WIDTH: u32 = 2560;
const PREVIEW_RGB_MAGIC: &[u8; 8] = b"CCPRGB8\n";

fn pending_preview_pngs() -> &'static Mutex<HashMap<PathBuf, JoinHandle<()>>> {
    static PENDING: OnceLock<Mutex<HashMap<PathBuf, JoinHandle<()>>>> = OnceLock::new();
    PENDING.get_or_init(|| Mutex::new(HashMap::new()))
}

fn join_pending_preview_png(path: &Path) {
    let handle = pending_preview_pngs()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .remove(path);
    if let Some(handle) = handle {
        let _ = handle.join();
    }
}

pub fn flush_preview_encodes() {
    let handles: Vec<JoinHandle<()>> = pending_preview_pngs()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .drain()
        .map(|(_, h)| h)
        .collect();
    for handle in handles {
        let _ = handle.join();
    }
}

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("{0}")]
    Message(String),
    #[error(transparent)]
    Core(#[from] ccp_core::Error),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
    #[error(transparent)]
    Image(#[from] image::ImageError),
}

pub type Result<T> = std::result::Result<T, Error>;

static LAST_APPLY_BACKEND: Mutex<String> = Mutex::new(String::new());
const LUT_CACHE_CAPACITY: usize = 8;
type LutCacheEntry = (Vec<u8>, Arc<Vec<f32>>);
type LutCache = Mutex<Vec<LutCacheEntry>>;

fn encoded_lut_cache() -> &'static LutCache {
    static CACHE: OnceLock<LutCache> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(Vec::new()))
}

fn encoded_lut_cached(
    model: &CorrectionModel,
    settings: &CorrectionSettings,
    size: usize,
) -> Result<Arc<Vec<f32>>> {
    let key = serde_json::to_vec(&(
        model,
        &settings.input_space,
        &settings.target_space,
        &settings.target_white,
        size,
    ))?;
    {
        let mut cache = encoded_lut_cache()
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if let Some(index) = cache.iter().position(|(candidate, _)| candidate == &key) {
            let hit = Arc::clone(&cache[index].1);
            if index + 1 != cache.len() {
                let entry = cache.remove(index);
                cache.push(entry);
            }
            return Ok(hit);
        }
    }
    let built = Arc::new(build_encoded_lut(model, settings, size)?);
    let mut cache = encoded_lut_cache()
        .lock()
        .unwrap_or_else(|e| e.into_inner());
    if let Some((_, hit)) = cache.iter().find(|(candidate, _)| candidate == &key) {
        return Ok(Arc::clone(hit));
    }
    if cache.len() >= LUT_CACHE_CAPACITY {
        cache.remove(0);
    }
    cache.push((key, Arc::clone(&built)));
    Ok(built)
}

pub fn last_apply_backend() -> String {
    LAST_APPLY_BACKEND
        .lock()
        .ok()
        .map(|g| g.clone())
        .unwrap_or_default()
}

fn remember_apply_backend(label: &str) {
    if let Ok(mut guard) = LAST_APPLY_BACKEND.lock() {
        *guard = label.to_string();
    }
}

fn apply_cc_accel(
    img: &mut Array3<f32>,
    model: &CorrectionModel,
    settings: &CorrectionSettings,
) -> Result<()> {
    let pixels = img.len() / 3;
    if pixels < APPLY_LUT_MIN_PIXELS {
        apply_cc_inplace(img, model, settings)?;
        remember_apply_backend("cpu");
        return Ok(());
    }
    let gs = APPLY_LUT_SIZE;
    let lut = encoded_lut_cached(model, settings, gs)?;
    // Still-image 3D LUT sampling is memory-local and benchmarks faster on CPU
    // than a host↔device round-trip. Explicit device policies remain available.
    let prefer = if runtime_policy() == DevicePolicy::Auto {
        "cpu".to_string()
    } else {
        runtime_assignment().apply
    };
    if matches!(prefer.as_str(), "directml" | "openvino") {
        if img.as_slice_mut().is_none() {
            let dim = img.raw_dim();
            let owned: Vec<f32> = img.iter().copied().collect();
            *img = Array3::from_shape_vec(dim, owned).map_err(|e| Error::Message(e.to_string()))?;
        }
        let (h, w, _) = img.dim();
        if let Some(slice) = img.as_slice_mut() {
            match apply_encoded_lut_ort(slice, h, w, &lut, gs) {
                Ok(backend) => {
                    remember_apply_backend(backend);
                    return Ok(());
                }
                Err(error) => {
                    eprintln!("ccp_bridge: LUT {prefer} inference failed; CPU fallback: {error}")
                }
            }
        }
    }
    if matches!(prefer.as_str(), "cuda" | "mps" | "auto") {
        if img.as_slice_mut().is_none() {
            let dim = img.raw_dim();
            let owned: Vec<f32> = img.iter().copied().collect();
            *img = Array3::from_shape_vec(dim, owned).map_err(|e| Error::Message(e.to_string()))?;
        }
        let (h, w, _) = img.dim();
        if let Some(slice) = img.as_slice_mut() {
            if let Ok(backend) = apply_encoded_lut_gpu(slice, h, w, &lut, gs, &prefer) {
                remember_apply_backend(backend);
                return Ok(());
            }
        }
    }
    apply_encoded_lut_inplace(img, &lut, gs);
    remember_apply_backend("cpu");
    Ok(())
}

#[derive(Clone, Debug, Default)]
pub struct ApplyImageReport {
    pub ffc_skipped: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SavedPipeline {
    pub version: u32,
    pub kind: String,
    pub do_ffc: bool,
    pub do_gc: bool,
    pub do_wb: bool,
    pub do_cc: bool,
    /// Version-1 polynomial field. Read indefinitely for model compatibility.
    pub gc_coeffs: Option<Vec<f64>>,
    /// Version-2 endpoint-safe monotonic lightness curve.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub gc_model: Option<GammaModel>,
    pub wb_diag: Option<[f64; 3]>,
    pub correction: Option<CorrectionModel>,
    pub input_space_id: String,
    pub input_white_id: String,
    pub target_space_id: String,
    pub target_white_id: String,
    pub gain_map_path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ffc_params: Option<FfcFitParams>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ffc_plane: Option<[i32; 4]>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ffc_plane_source: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ffc_white_size: Option<[i32; 2]>,
}

impl Default for SavedPipeline {
    fn default() -> Self {
        Self {
            version: 2,
            kind: ModelKind::RootPolynomial2.name().into(),
            do_ffc: false,
            do_gc: true,
            do_wb: true,
            do_cc: true,
            gc_coeffs: None,
            gc_model: None,
            wb_diag: None,
            correction: None,
            input_space_id: "srgb".into(),
            input_white_id: "d65".into(),
            target_space_id: "srgb".into(),
            target_white_id: "d65".into(),
            gain_map_path: None,
            ffc_params: None,
            ffc_plane: None,
            ffc_plane_source: None,
            ffc_white_size: None,
        }
    }
}

impl SavedPipeline {
    pub fn lut_pipeline(&self) -> Result<(LutPipeline, CorrectionSettings)> {
        let settings = correction_settings(
            &self.input_space_id,
            &self.input_white_id,
            &self.target_space_id,
            &self.target_white_id,
        )?;
        validate_stage_input(&settings, self.do_ffc, self.do_gc, self.do_wb)?;
        let mut lut = LutPipeline::default();
        if self.do_gc {
            if let Some(model) = &self.gc_model {
                lut.gc = Some(model.clone());
            } else if let Some(coeffs) = &self.gc_coeffs {
                lut.gc = Some(GammaModel::legacy(coeffs.clone()));
            }
        }
        if self.do_wb {
            if let Some(diag) = self.wb_diag {
                lut.wb = Some(WhiteBalance { diag });
            }
        }
        if self.do_cc {
            if let Some(model) = &self.correction {
                lut.cc = Some((model.clone(), settings.clone()));
            }
        }
        Ok((lut, settings))
    }

    pub fn apply_color(&self, mut rgb: [f64; 3]) -> Result<[f64; 3]> {
        let (lut, _) = self.lut_pipeline()?;
        if let Some(gc) = &lut.gc {
            rgb = apply_gc_pixel(rgb, gc)?;
        }
        if let Some(wb) = &lut.wb {
            rgb = apply_wb_pixel(rgb, wb);
        }
        if let Some((model, settings)) = &lut.cc {
            rgb = apply_correction_pixel(model, rgb, settings)?;
        }
        Ok(rgb)
    }
}

#[derive(Clone, Debug)]
pub struct HandshakeFace {
    pub chart_type: String,
    pub cli_chart: String,
    pub patch_obbs: Vec<Quad>,
}

#[derive(Clone, Debug)]
pub struct RunOutcome {
    pub output: PathBuf,
    pub report: PathBuf,
    pub model_out: PathBuf,
    pub lut_out: Option<PathBuf>,
    pub preview_dir: Option<PathBuf>,
    pub report_json: Value,
    pub saved: SavedPipeline,
    pub chart_type: String,
    pub has_delta_e: bool,
}

pub fn json_str<'a>(v: &'a Value, keys: &[&str]) -> Option<&'a str> {
    for key in keys {
        if let Some(s) = v.get(*key).and_then(Value::as_str) {
            if !s.is_empty() {
                return Some(s);
            }
        }
    }
    None
}

pub fn json_bool(v: &Value, keys: &[&str], default: bool) -> bool {
    for key in keys {
        if let Some(b) = v.get(*key).and_then(Value::as_bool) {
            return b;
        }
    }
    default
}

pub fn json_f64(v: &Value, keys: &[&str], default: f64) -> f64 {
    for key in keys {
        if let Some(n) = v.get(*key).and_then(Value::as_f64) {
            return n;
        }
        if let Some(n) = v.get(*key).and_then(Value::as_i64) {
            return n as f64;
        }
    }
    default
}

pub fn json_i64(v: &Value, keys: &[&str], default: i64) -> i64 {
    for key in keys {
        if let Some(n) = v.get(*key).and_then(Value::as_i64) {
            return n;
        }
        if let Some(n) = v.get(*key).and_then(Value::as_f64) {
            return n as i64;
        }
    }
    default
}

fn parse_xy(v: &Value) -> Option<Vec2> {
    if v.is_array() && v.as_array()?.len() >= 2 {
        let x = v[0].as_f64()?;
        let y = v[1].as_f64()?;
        return Some(Vec2 { x, y });
    }
    if v.is_object() {
        let x = v.get("x")?.as_f64()?;
        let y = v.get("y")?.as_f64()?;
        return Some(Vec2 { x, y });
    }
    None
}

fn parse_quad(v: &Value) -> Option<Quad> {
    if v.is_array() && v.as_array()?.len() >= 4 {
        return Some(Quad {
            top_left: parse_xy(&v[0])?,
            top_right: parse_xy(&v[1])?,
            bottom_right: parse_xy(&v[2])?,
            bottom_left: parse_xy(&v[3])?,
        });
    }
    if let Some(obj) = v.as_object() {
        if let (Some(tl), Some(tr), Some(br), Some(bl)) = (
            obj.get("top_left").and_then(parse_xy),
            obj.get("top_right").and_then(parse_xy),
            obj.get("bottom_right").and_then(parse_xy),
            obj.get("bottom_left").and_then(parse_xy),
        ) {
            return Some(Quad {
                top_left: tl,
                top_right: tr,
                bottom_right: br,
                bottom_left: bl,
            });
        }
    }
    None
}

fn parse_quads(face: &Value, name: &str) -> Vec<Quad> {
    face.get(name)
        .and_then(Value::as_array)
        .map(|arr| arr.iter().filter_map(parse_quad).collect())
        .unwrap_or_default()
}

fn scale_quad(q: Quad, crop: f64) -> Quad {
    let k = crop.max(0.02);
    let c = Vec2 {
        x: 0.25 * (q.top_left.x + q.top_right.x + q.bottom_right.x + q.bottom_left.x),
        y: 0.25 * (q.top_left.y + q.top_right.y + q.bottom_right.y + q.bottom_left.y),
    };
    let s = |p: Vec2| Vec2 {
        x: c.x + (p.x - c.x) * k,
        y: c.y + (p.y - c.y) * k,
    };
    Quad {
        top_left: s(q.top_left),
        top_right: s(q.top_right),
        bottom_right: s(q.bottom_right),
        bottom_left: s(q.bottom_left),
    }
}

fn face_from_value(face: &Value, crop: f64) -> Option<HandshakeFace> {
    let chart_type = json_str(face, &["chart_type", "cli_chart"])
        .unwrap_or("classic")
        .to_string();
    let cli_chart = json_str(face, &["cli_chart", "chart_type"])
        .unwrap_or(&chart_type)
        .to_string();
    let mut patch_obbs = parse_quads(face, "patch_obbs");
    if patch_obbs.is_empty() {
        patch_obbs = parse_quads(face, "patch_cells")
            .into_iter()
            .map(|q| scale_quad(q, crop))
            .collect();
    }
    if patch_obbs.is_empty() {
        return None;
    }
    Some(HandshakeFace {
        chart_type,
        cli_chart,
        patch_obbs,
    })
}

/// Parse Opt2/manual handshake JSON. `face` selects `charts[i]`; `use_all` keeps every face.
pub fn parse_handshake(
    root: &Value,
    face: usize,
    use_all: bool,
    crop: f64,
) -> Result<Vec<HandshakeFace>> {
    if let Some(charts) = root.get("charts").and_then(Value::as_array) {
        if !charts.is_empty() {
            if use_all {
                let faces: Vec<HandshakeFace> = charts
                    .iter()
                    .filter_map(|c| face_from_value(c, crop))
                    .collect();
                if faces.is_empty() {
                    return Err(Error::Message("handshake charts have no patch_obbs".into()));
                }
                return Ok(faces);
            }
            let idx = face.min(charts.len() - 1);
            return face_from_value(&charts[idx], crop)
                .map(|f| vec![f])
                .ok_or_else(|| Error::Message("handshake face has no patch_obbs".into()));
        }
    }
    face_from_value(root, crop)
        .map(|f| vec![f])
        .ok_or_else(|| Error::Message("handshake has no patch_obbs / patch_cells".into()))
}

pub fn load_handshake_value(req: &Value) -> Result<Value> {
    if let Some(obj) = req.get("handshake").filter(|v| v.is_object()) {
        return Ok(obj.clone());
    }
    for key in ["handshake", "chart_json", "handshake_path", "chart_path"] {
        if let Some(path) = req.get(key).and_then(Value::as_str) {
            if path.is_empty() || path.starts_with('{') {
                continue;
            }
            let text = std::fs::read_to_string(path)?;
            return Ok(serde_json::from_str(&text)?);
        }
    }
    if let Some(s) = req.get("handshake").and_then(Value::as_str) {
        if s.trim_start().starts_with('{') {
            return Ok(serde_json::from_str(s)?);
        }
    }
    Err(Error::Message(
        "run requires handshake or chart_json (path or object)".into(),
    ))
}

pub fn load_neutrals() -> HashMap<String, Vec<String>> {
    if let Some(root) = ccp_detect::find_ccp_new_ui_root() {
        let path = root.join("refs").join("neutrals.json");
        if let Ok(table) = load_neutrals_table(Some(&path)) {
            return table;
        }
    }
    load_neutrals_table(None).unwrap_or_else(|_| ccp_core::refs::embedded_neutrals_table())
}

pub fn rgb_image_to_array3(img: &RgbImage) -> Result<Array3<f32>> {
    Array3::from_shape_vec(
        (img.height as usize, img.width as usize, 3),
        img.rgb.clone(),
    )
    .map_err(|e| Error::Message(e.to_string()))
}

pub fn rgb_image_into_array3(img: RgbImage) -> Result<Array3<f32>> {
    Array3::from_shape_vec((img.height as usize, img.width as usize, 3), img.rgb)
        .map_err(|e| Error::Message(e.to_string()))
}

fn array3_f32s(arr: &Array3<f32>) -> std::borrow::Cow<'_, [f32]> {
    if let Some(slice) = arr.as_slice() {
        std::borrow::Cow::Borrowed(slice)
    } else {
        std::borrow::Cow::Owned(arr.iter().copied().collect())
    }
}

fn f32_to_u8(v: f32) -> u8 {
    (v.clamp(0.0, 1.0) * 255.0 + 0.5) as u8
}

pub fn array3_to_rgb_image(arr: &Array3<f32>) -> Result<RgbImage> {
    let height = arr.shape()[0] as u32;
    let width = arr.shape()[1] as u32;
    Ok(RgbImage::new(width, height, array3_f32s(arr).into_owned())?)
}

fn array3_to_rgb8(arr: &Array3<f32>) -> Result<Rgb8> {
    let h = arr.shape()[0] as u32;
    let w = arr.shape()[1] as u32;
    let src = array3_f32s(arr);
    let mut buf = vec![0u8; src.len()];
    buf.par_iter_mut()
        .zip(src.par_iter())
        .for_each(|(d, s)| *d = f32_to_u8(*s));
    Rgb8::from_raw(w, h, buf).ok_or_else(|| Error::Message("preview buffer size mismatch".into()))
}

fn preview_rgb8(arr: &Array3<f32>) -> Result<Rgb8> {
    let h = arr.shape()[0];
    let w = arr.shape()[1];
    let src = array3_f32s(arr);
    if w as u32 <= PREVIEW_MAX_WIDTH {
        let mut buf = vec![0u8; src.len()];
        buf.par_iter_mut()
            .zip(src.par_iter())
            .for_each(|(d, s)| *d = f32_to_u8(*s));
        return Rgb8::from_raw(w as u32, h as u32, buf)
            .ok_or_else(|| Error::Message("preview buffer size mismatch".into()));
    }

    let out_w = PREVIEW_MAX_WIDTH as usize;
    let out_h = ((h * out_w) / w).max(1);
    let mut buf = vec![0u8; out_w * out_h * 3];
    buf.par_chunks_exact_mut(out_w * 3)
        .enumerate()
        .for_each(|(y, row)| {
            let sy = y * h / out_h;
            let src_row = sy * w * 3;
            for x in 0..out_w {
                let sx = x * w / out_w;
                let si = src_row + sx * 3;
                let di = x * 3;
                row[di] = f32_to_u8(src[si]);
                row[di + 1] = f32_to_u8(src[si + 1]);
                row[di + 2] = f32_to_u8(src[si + 2]);
            }
        });
    Rgb8::from_raw(out_w as u32, out_h as u32, buf)
        .ok_or_else(|| Error::Message("preview buffer size mismatch".into()))
}

fn encode_jpeg(rgb: &Rgb8, path: &Path, quality: u8) -> Result<()> {
    let width = u16::try_from(rgb.width())
        .map_err(|_| Error::Message(format!("JPEG width {} exceeds 65535 pixels", rgb.width())))?;
    let height = u16::try_from(rgb.height()).map_err(|_| {
        Error::Message(format!("JPEG height {} exceeds 65535 pixels", rgb.height()))
    })?;
    let file = std::fs::File::create(path)?;
    let mut encoder = JpegEncoder::new(BufWriter::new(file), quality);
    // Match the previous product encoder's full-chroma output. This avoids introducing
    // chroma loss while using the focused SIMD-friendly encoder implementation.
    encoder.set_sampling_factor(SamplingFactor::R_4_4_4);
    encoder
        .encode(rgb.as_raw(), width, height, JpegColorType::Rgb)
        .map_err(|e| Error::Message(format!("JPEG encode failed: {e}")))
}

pub fn write_preview_jpeg(arr: &Array3<f32>, path: impl AsRef<Path>) -> Result<()> {
    let path = path.as_ref();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let rgb = preview_rgb8(arr)?;
    encode_jpeg(&rgb, path, 72)
}

pub fn write_preview_png(arr: &Array3<f32>, path: impl AsRef<Path>) -> Result<()> {
    let path = path.as_ref();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    encode_preview_png(&preview_rgb8(arr)?, path)
}

fn write_preview_rgb_file(rgb: &Rgb8, path: &Path) -> Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut file = std::fs::File::create(path)?;
    file.write_all(PREVIEW_RGB_MAGIC)?;
    file.write_all(&rgb.width().to_le_bytes())?;
    file.write_all(&rgb.height().to_le_bytes())?;
    file.write_all(rgb.as_raw())?;
    Ok(())
}

fn encode_preview_png(rgb: &Rgb8, path: &Path) -> Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let file = std::fs::File::create(path)?;
    let enc = PngEncoder::new_with_quality(
        std::io::BufWriter::new(file),
        CompressionType::Fast,
        FilterType::NoFilter,
    );
    enc.write_image(
        rgb.as_raw(),
        rgb.width(),
        rgb.height(),
        ExtendedColorType::Rgb8,
    )?;
    Ok(())
}

fn spawn_preview_png(rgb: Rgb8, path: PathBuf) {
    join_pending_preview_png(&path);
    let key = path.clone();
    if let Ok(handle) = std::thread::Builder::new()
        .name("ccp-preview-png".into())
        .spawn(move || {
            let _ = encode_preview_png(&rgb, &path);
        })
    {
        pending_preview_pngs()
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .insert(key, handle);
    }
}

fn write_preview(arr: &Array3<f32>, path: impl AsRef<Path>) -> Result<()> {
    write_preview_in_space(arr, path, ccp_core::color::srgb_d65_space().1)
}

fn preview_rgb8_in_space(arr: &Array3<f32>, space: &RgbColorSpace) -> Result<Rgb8> {
    if space.id == "srgb" && space.white.id == "d65" {
        return preview_rgb8(arr);
    }
    let target = ccp_core::color::srgb_d65_space().1;
    let h = arr.shape()[0];
    let w = arr.shape()[1];
    let out_w = w.min(PREVIEW_MAX_WIDTH as usize);
    let out_h = ((h * out_w) / w).max(1);
    let src = array3_f32s(arr);
    let mut buf = vec![0u8; out_w * out_h * 3];
    // Transform floating source samples, not already quantized 8-bit output.
    // Export pixels are never modified by the display-only conversion.
    buf.par_chunks_exact_mut(out_w * 3)
        .enumerate()
        .try_for_each(|(y, row)| -> Result<()> {
            let sy = y * h / out_h;
            for x in 0..out_w {
                let i = (sy * w + x * w / out_w) * 3;
                let rgb = ccp_core::color::convert_encoded_rgb(
                    [src[i] as f64, src[i + 1] as f64, src[i + 2] as f64],
                    space,
                    target,
                )?;
                for c in 0..3 {
                    row[x * 3 + c] = f32_to_u8(rgb[c] as f32);
                }
            }
            Ok(())
        })?;
    Rgb8::from_raw(out_w as u32, out_h as u32, buf)
        .ok_or_else(|| Error::Message("preview buffer size mismatch".into()))
}

fn write_preview_in_space(
    arr: &Array3<f32>,
    path: impl AsRef<Path>,
    space: &RgbColorSpace,
) -> Result<()> {
    let path = path.as_ref();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let rgb = preview_rgb8_in_space(arr, space)?;
    write_preview_rgb_file(&rgb, &path.with_extension("rgb"))?;
    spawn_preview_png(rgb, path.to_path_buf());
    Ok(())
}

fn preview_path(dir: &Path, stem: &str) -> PathBuf {
    dir.join(format!("{stem}.png"))
}

pub fn write_still(arr: &Array3<f32>, path: impl AsRef<Path>) -> Result<()> {
    let path = path.as_ref();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let rgb = array3_to_rgb8(arr)?;
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    if ext == "png" {
        let file = std::fs::File::create(path)?;
        let enc = PngEncoder::new_with_quality(
            std::io::BufWriter::new(file),
            CompressionType::Fast,
            FilterType::NoFilter,
        );
        enc.write_image(
            rgb.as_raw(),
            rgb.width(),
            rgb.height(),
            ExtendedColorType::Rgb8,
        )?;
    } else {
        encode_jpeg(&rgb, path, 90)?;
    }
    Ok(())
}

fn validate_stage_input(
    settings: &CorrectionSettings,
    ffc: bool,
    gc: bool,
    wb: bool,
) -> Result<()> {
    if (ffc || gc || wb)
        && (settings.input_space.id != "srgb" || settings.input_space.white.id != "d65")
    {
        return Err(Error::Message(
            "FFC, GC and WB require encoded sRGB / D65 input. Convert the source image to sRGB / D65 first, or disable FFC, GC and WB to use CC-only with another input space. Changing the selector does not convert the source pixels.".into(),
        ));
    }
    Ok(())
}

pub fn correction_settings(
    input_space: &str,
    input_white: &str,
    target_space: &str,
    target_white: &str,
) -> Result<CorrectionSettings> {
    let in_white = parse_white_point(input_white)?;
    let tgt_white = parse_white_point(target_white)?;
    Ok(CorrectionSettings {
        input_space: parse_rgb_color_space(input_space, &in_white)?,
        target_space: parse_rgb_color_space(target_space, &tgt_white)?,
        target_white: tgt_white,
    })
}

fn ffc_params_from_req(req: &Value) -> FfcFitParams {
    let ffc = req.get("ffc").cloned().unwrap_or(Value::Null);
    let method = json_str(&ffc, &["method", "fit_method"])
        .or_else(|| json_str(req, &["ffc_method"]))
        .unwrap_or("poly");
    let mut smooth = json_i64(&ffc, &["smooth", "smooth_window"], 5) as i32;
    if smooth % 2 == 0 {
        smooth += 1;
    }
    FfcFitParams {
        method: match method.to_ascii_lowercase().as_str() {
            "linear" => FfcFitMethod::Linear,
            "pls" => FfcFitMethod::Pls,
            "nn" => FfcFitMethod::Nn,
            _ => FfcFitMethod::Poly,
        },
        bins: json_i64(&ffc, &["bins"], 50).max(2) as usize,
        smooth_window: smooth.max(1),
        degree: json_i64(&ffc, &["degree"], 5).max(1) as usize,
        upsample: Upsample::Cubic,
        nn_max_iter: json_i64(&ffc, &["nn_max_iter", "max_iter"], 10_000).max(1) as usize,
        tol: {
            let v = json_f64(&ffc, &["tol"], json_f64(req, &["ffc_tol"], 1e-8));
            if v.is_finite() && v > 0.0 {
                v
            } else {
                1e-8
            }
        },
        cut_off: json_f64(
            &ffc,
            &["cut_off", "ffc_cut_off"],
            json_f64(req, &["ffc_cut_off"], 1.3),
        ) as f32,
        max_val: json_f64(
            &ffc,
            &["max_val", "ffc_max_val"],
            json_f64(req, &["ffc_max_val"], 1.8),
        ) as f32,
    }
}

fn mapping_from_req(req: &Value) -> MappingOptions {
    let defaults = MappingOptions::default();
    let include_combinations = json_bool(req, &["include_combinations"], true);
    let poly_degree = json_i64(req, &["degree", "poly_degree", "fit_samples_degree"], 2) as i32;
    let pls = req.get("pls").cloned().unwrap_or(Value::Null);
    let pls_components = json_i64(
        &pls,
        &["components", "pls_components"],
        json_i64(req, &["pls_components"], 0),
    ) as i32;
    let nn = req.get("nn").cloned().unwrap_or(Value::Null);
    let hidden_size = json_i64(&nn, &["hidden_size"], json_i64(req, &["hidden_size"], 100)) as i32;
    let hidden_layers_value = nn.get("hidden_layers").or_else(|| req.get("hidden_layers"));
    let mut hidden_layers: Vec<i32> = hidden_layers_value
        .and_then(Value::as_array)
        .map(|values| {
            values
                .iter()
                .filter_map(Value::as_i64)
                .take(4)
                .map(|width| width.clamp(4, 512) as i32)
                .collect()
        })
        .unwrap_or_default();
    if hidden_layers.is_empty() {
        hidden_layers.push(hidden_size.clamp(4, 512));
    }
    let nn_optimizer = json_str(&nn, &["optimizer", "nn_optimizer"])
        .or_else(|| json_str(req, &["nn_optimizer"]))
        .unwrap_or("adam")
        .to_ascii_lowercase();
    let nn_batch_norm = json_bool(
        &nn,
        &["batch_norm", "batch_normalization"],
        json_bool(req, &["nn_batch_norm", "nn_batch_normalization"], false),
    );
    let nn_dropout = json_f64(
        &nn,
        &["dropout", "drop_rate"],
        json_f64(req, &["nn_dropout", "drop_rate"], 0.0),
    )
    .clamp(0.0, 0.8);
    let nn_epochs = json_i64(
        &nn,
        &["epochs", "nn_epochs"],
        json_i64(req, &["nn_epochs"], 10000),
    ) as i32;
    let nn_patience = json_i64(
        &nn,
        &["patience", "nn_patience"],
        json_i64(req, &["nn_patience"], 1500),
    ) as i32;
    let learning_rate = json_f64(
        &nn,
        &["lr", "learning_rate"],
        json_f64(req, &["nn_lr"], 0.001),
    );
    let tol = json_f64(
        &nn,
        &["tol", "nn_tol"],
        json_f64(req, &["tol", "nn_tol"], 1e-6),
    );
    let tol = if tol.is_finite() && tol > 0.0 {
        tol
    } else {
        1e-6
    };
    MappingOptions {
        extrapolate_saturated: defaults.extrapolate_saturated,
        saturation_threshold: defaults.saturation_threshold,
        poly_degree,
        include_combinations,
        pls_components,
        hidden_size: hidden_layers[0],
        hidden_layers,
        nn_optimizer,
        nn_batch_norm,
        nn_dropout,
        nn_epochs,
        nn_patience,
        learning_rate,
        tol,
        clip_lo: json_f64(req, &["clip_lo", "rgb_clip_lo"], -0.2),
        clip_hi: json_f64(req, &["clip_hi", "rgb_clip_hi"], 1.3),
    }
}

fn spaces_from_req(req: &Value) -> (String, String, String, String) {
    let spaces = req.get("spaces").cloned().unwrap_or(Value::Null);
    let whites = req.get("whites").cloned().unwrap_or(Value::Null);
    let input_space = json_str(&spaces, &["input", "input_space"])
        .or_else(|| json_str(req, &["input_space"]))
        .unwrap_or("srgb")
        .to_string();
    let target_space = json_str(&spaces, &["target", "target_space"])
        .or_else(|| json_str(req, &["target_space"]))
        .unwrap_or("srgb")
        .to_string();
    let input_white = json_str(&whites, &["input", "input_white"])
        .or_else(|| json_str(req, &["input_white"]))
        .unwrap_or("d65")
        .to_string();
    let target_white = json_str(&whites, &["target", "target_white"])
        .or_else(|| json_str(req, &["target_white"]))
        .unwrap_or("d65")
        .to_string();
    (input_space, input_white, target_space, target_white)
}

fn sampling_from_req(req: &Value) -> SamplingOptions {
    let n = json_i64(req, &["fit_samples"], 9) as i32;
    SamplingOptions {
        samples_per_axis: if n <= 0 { 9 } else { n },
        inner_fraction: 0.70,
    }
}

fn sample_faces(
    image: RgbView<'_>,
    faces: &[HandshakeFace],
    options: SamplingOptions,
) -> Result<(ChartSpec, Vec<ChartPatch>, String)> {
    let mut all = Vec::new();
    let mut spec = None;
    let mut chart_type = String::new();
    for face in faces {
        let name = if face.cli_chart.is_empty() {
            face.chart_type.as_str()
        } else {
            face.cli_chart.as_str()
        };
        let this = chart_spec_from_name(name)?;
        if chart_type.is_empty() {
            chart_type = this.id.clone();
        }
        let sampled = sample_patch_obbs_view(&image, &this, &face.patch_obbs, options)?;
        all.extend(sampled);
        if spec.is_none() {
            spec = Some(this);
        }
    }
    let spec = spec.ok_or_else(|| Error::Message("no handshake faces".into()))?;
    Ok((spec, all, chart_type))
}

fn sample_work(
    work: &Array3<f32>,
    faces: &[HandshakeFace],
    options: SamplingOptions,
) -> Result<(ChartSpec, Vec<ChartPatch>, String)> {
    let pixels = array3_f32s(work);
    let view = RgbView::from_rgb(
        work.shape()[1] as u32,
        work.shape()[0] as u32,
        pixels.as_ref(),
    )?;
    sample_faces(view, faces, options)
}

fn sample_work_ffc(
    work: &Array3<f32>,
    faces: &[HandshakeFace],
    options: SamplingOptions,
    gain: &Array2<f32>,
) -> Result<(ChartSpec, Vec<ChartPatch>, String)> {
    let pixels = array3_f32s(work);
    let width = work.shape()[1] as u32;
    let height = work.shape()[0] as u32;
    let view = RgbView::from_rgb(width, height, pixels.as_ref())?;
    let transform = |rgb, x, y| apply_ffc_pixel(rgb, sample_gain_at(gain, x, y, width, height));
    let mut all = Vec::new();
    let mut spec = None;
    let mut chart_type = String::new();
    for face in faces {
        let name = if face.cli_chart.is_empty() {
            face.chart_type.as_str()
        } else {
            face.cli_chart.as_str()
        };
        let this = chart_spec_from_name(name)?;
        if chart_type.is_empty() {
            chart_type = this.id.clone();
        }
        all.extend(sample_patch_obbs_view_transform(
            &view,
            &this,
            &face.patch_obbs,
            options,
            &transform,
        )?);
        if spec.is_none() {
            spec = Some(this);
        }
    }
    Ok((
        spec.ok_or_else(|| Error::Message("no handshake faces".into()))?,
        all,
        chart_type,
    ))
}

fn median(mut values: Vec<f64>) -> f64 {
    if values.is_empty() {
        return 0.0;
    }
    values.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let mid = values.len() / 2;
    if values.len().is_multiple_of(2) {
        (values[mid - 1] + values[mid]) * 0.5
    } else {
        values[mid]
    }
}

/// Mean / median / max CIEDE2000 on already-sampled handshake patches.
/// Cheap: Lab on tens of patches, never another full-image pass.
fn de_stats(patches: &[ChartPatch], use_original: bool, space: &RgbColorSpace) -> (f64, f64, f64) {
    let mut vals = Vec::with_capacity(patches.len());
    for p in patches {
        let rgb = if use_original {
            p.original_measured_srgb
        } else {
            p.measured_srgb
        };
        if let Ok(de) = delta_e00_in_space(rgb, p.reference_lab_d50, space) {
            if de.is_finite() {
                vals.push(de);
            }
        }
    }
    let mean = if vals.is_empty() {
        f64::NAN
    } else {
        vals.iter().sum::<f64>() / vals.len() as f64
    };
    let max = vals.iter().copied().fold(f64::NAN, f64::max);
    let med = if vals.is_empty() {
        f64::NAN
    } else {
        median(vals)
    };
    (mean, med, max)
}

fn stage_json(name: &str, before: (f64, f64, f64), after: (f64, f64, f64), elapsed: f64) -> Value {
    let improve = if before.0.is_finite() && after.0.is_finite() && before.0 > 1e-12 {
        (before.0 - after.0) / before.0 * 100.0
    } else {
        0.0
    };
    json!({
        "name": name,
        "mean_before": before.0,
        "mean_after": after.0,
        "median_before": before.1,
        "median_after": after.1,
        "max_before": before.2,
        "max_after": after.2,
        "improvement_percent": improve,
        "elapsed_secs": elapsed,
    })
}

fn build_report(
    model_name: &str,
    feature_count: i32,
    before: &[ChartPatch],
    after: &[ChartPatch],
    has_chart: bool,
    input_space: &RgbColorSpace,
    output_space: &RgbColorSpace,
) -> Value {
    if !has_chart || before.is_empty() {
        return json!({
            "model": model_name,
            "feature_count": feature_count,
            "saturated_patches": 0,
            "metrics_hidden": true,
        });
    }
    let mut patches = Vec::new();
    let mut before_v = Vec::new();
    let mut after_v = Vec::new();
    let display_space = ccp_core::color::srgb_d65_space().1;
    for (b, a) in before.iter().zip(after.iter()) {
        let db = delta_e00_in_space(b.original_measured_srgb, b.reference_lab_d50, input_space)
            .unwrap_or(f64::NAN);
        let da = delta_e00_in_space(a.measured_srgb, a.reference_lab_d50, output_space)
            .unwrap_or(f64::NAN);
        if db.is_finite() {
            before_v.push(db);
        }
        if da.is_finite() {
            after_v.push(da);
        }
        patches.push(json!({
            "name": b.name,
            "index": b.index,
            "before_delta_e": db,
            "after_delta_e": da,
            "saturated": b.saturated,
            "measured_srgb": b.original_measured_srgb,
            "reference_srgb": b.reference_srgb,
            "corrected_srgb": a.measured_srgb,
            "measured_display_srgb": ccp_core::color::convert_encoded_rgb(
                b.original_measured_srgb, input_space, display_space).ok(),
            "reference_display_srgb": b.reference_srgb,
            "corrected_display_srgb": ccp_core::color::convert_encoded_rgb(
                a.measured_srgb, output_space, display_space).ok(),
        }));
    }
    let mean = |v: &[f64]| {
        if v.is_empty() {
            f64::NAN
        } else {
            v.iter().sum::<f64>() / v.len() as f64
        }
    };
    let max = |v: &[f64]| v.iter().copied().fold(f64::NAN, f64::max);
    json!({
        "model": model_name,
        "feature_count": feature_count,
        "saturated_patches": before.iter().filter(|p| p.saturated).count(),
        "metrics_hidden": false,
        "colorimetry": {
            "metric": "CIEDE2000", "evaluation_white": "d65", "observer_degrees": 2,
            "reference_white": "d50", "chromatic_adaptation": "Bradford",
            "input_space": input_space.id, "input_white": input_space.white.id,
            "output_space": output_space.id, "output_white": output_space.white.id,
            "rgb_fields_note": "Legacy measured_srgb/corrected_srgb contain declared input/output RGB; reference_srgb and all *_display_srgb fields are display sRGB/D65",
            "display_space": "srgb", "display_white": "d65"
        },
        "delta_e_2000": {
            "mean_before": mean(&before_v),
            "mean_after": mean(&after_v),
            "median_before": median(before_v.clone()),
            "median_after": median(after_v.clone()),
            "max_before": max(&before_v),
            "max_after": max(&after_v),
        },
        "patches": patches,
    })
}

fn maybe_preview(dir: Option<&Path>, name: &str, arr: &Array3<f32>) -> Result<()> {
    if name == "original" {
        return Ok(());
    }
    if let Some(dir) = dir {
        write_preview(arr, preview_path(dir, name))?;
    }
    Ok(())
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum PreviewMode {
    None,
    Corrected,
    All,
    Stage(String),
}

impl PreviewMode {
    pub fn from_req(req: &Value) -> Self {
        match json_str(req, &["preview_mode"])
            .unwrap_or("")
            .to_ascii_lowercase()
            .as_str()
        {
            "none" | "off" | "false" => Self::None,
            "all" => Self::All,
            "corrected" | "preview" => Self::Corrected,
            "" => Self::Corrected,
            other => Self::Stage(other.to_string()),
        }
    }

    fn is_all(&self) -> bool {
        matches!(self, Self::All)
    }

    fn wants_intermediate(&self, name: &str) -> bool {
        self.is_all() && name != "original" && name != "corrected" && name != "cc"
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct FfcGainKey {
    white: PathBuf,
    mtime_nanos: u128,
    len: u64,
    method: String,
    bins: usize,
    smooth_window: i32,
    degree: usize,
    upsample: String,
    nn_max_iter: usize,
    tol_bits: u64,
    cut_off_bits: u32,
    max_val_bits: u32,
    crop: String,
}

impl FfcGainKey {
    fn new(white: &Path, crop: Option<&str>, params: &FfcFitParams) -> Self {
        let meta = std::fs::metadata(white).ok();
        let mtime = meta
            .as_ref()
            .and_then(|m| m.modified().ok())
            .and_then(|t| t.duration_since(SystemTime::UNIX_EPOCH).ok())
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        let len = meta.map(|m| m.len()).unwrap_or(0);
        Self {
            white: std::fs::canonicalize(white).unwrap_or_else(|_| white.to_path_buf()),
            mtime_nanos: mtime,
            len,
            method: format!("{:?}", params.method),
            bins: params.bins,
            smooth_window: params.smooth_window,
            degree: params.degree,
            upsample: format!("{:?}", params.upsample),
            nn_max_iter: params.nn_max_iter,
            tol_bits: params.tol.to_bits(),
            cut_off_bits: params.cut_off.to_bits(),
            max_val_bits: params.max_val.to_bits(),
            crop: crop.unwrap_or("").to_string(),
        }
    }
}

#[derive(Clone, Debug)]
pub struct CachedFfc {
    pub gain: Arc<Array2<f32>>,
    pub plane: [i32; 4],
    pub plane_source: String,
    pub white_size: [i32; 2],
}

impl CachedFfc {
    pub fn compute(white: &Path, crop: Option<&str>, params: &FfcFitParams) -> Result<Self> {
        let white_img = load_image(white)?;
        let (rect, source) = ffc_plane_info(&white_img, crop);
        let white_size = [white_img.width as i32, white_img.height as i32];
        let white_arr = rgb_image_into_array3(white_img)?;
        let gain = compute_multiplier(Some(&white_arr), Some(rect), params)?;
        Ok(Self {
            gain: Arc::new(gain),
            plane: rect,
            plane_source: source.to_string(),
            white_size,
        })
    }
}

#[derive(Debug)]
pub struct FfcGainCache {
    inner: Mutex<Option<(FfcGainKey, Arc<CachedFfc>)>>,
}

impl Default for FfcGainCache {
    fn default() -> Self {
        Self {
            inner: Mutex::new(None),
        }
    }
}

impl FfcGainCache {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn get_or_compute(
        &self,
        white: &Path,
        crop: Option<&str>,
        params: &FfcFitParams,
    ) -> Result<Arc<CachedFfc>> {
        let key = FfcGainKey::new(white, crop, params);
        {
            let guard = self.inner.lock().unwrap_or_else(|e| e.into_inner());
            if let Some((k, v)) = guard.as_ref() {
                if k == &key {
                    return Ok(Arc::clone(v));
                }
            }
        }
        let computed = Arc::new(CachedFfc::compute(white, crop, params)?);
        let mut guard = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        if let Some((k, v)) = guard.as_ref() {
            if k == &key {
                return Ok(Arc::clone(v));
            }
        }
        *guard = Some((key, Arc::clone(&computed)));
        Ok(computed)
    }
}

fn resolve_cached_ffc(
    white: &Path,
    crop: Option<&str>,
    params: &FfcFitParams,
    cache: Option<&Arc<FfcGainCache>>,
) -> Result<Arc<CachedFfc>> {
    match cache {
        Some(c) => c.get_or_compute(white, crop, params),
        None => CachedFfc::compute(white, crop, params).map(Arc::new),
    }
}

fn write_jobs_parallel(
    work: &Array3<f32>,
    jobs: &[(PathBuf, bool)],
    space: &RgbColorSpace,
) -> Result<()> {
    if jobs.is_empty() {
        return Ok(());
    }
    if jobs.len() == 1 {
        let (path, preview) = &jobs[0];
        return if *preview {
            write_preview_in_space(work, path, space)
        } else {
            write_still(work, path)
        };
    }
    let errors: Vec<String> = jobs
        .par_iter()
        .filter_map(|(path, preview)| {
            let r = if *preview {
                write_preview_in_space(work, path, space)
            } else {
                write_still(work, path)
            };
            r.err().map(|e| e.to_string())
        })
        .collect();
    if let Some(e) = errors.into_iter().next() {
        return Err(Error::Message(e));
    }
    Ok(())
}

fn finish_image_writes(
    work: &Array3<f32>,
    output: &Path,
    preview_dir: Option<&Path>,
    write_output: bool,
    preview_mode: &PreviewMode,
    space: &RgbColorSpace,
) -> Result<()> {
    let mut jobs: Vec<(PathBuf, bool)> = Vec::new();
    if write_output {
        jobs.push((output.to_path_buf(), false));
    }
    if let Some(dir) = preview_dir {
        match preview_mode {
            PreviewMode::None => {}
            PreviewMode::Corrected => {
                jobs.push((preview_path(dir, "corrected"), true));
            }
            PreviewMode::Stage(stage) => {
                if stage != "original" {
                    let stem = if stage == "corrected" || stage == "cc" {
                        "corrected"
                    } else {
                        stage.as_str()
                    };
                    jobs.push((preview_path(dir, stem), true));
                }
            }
            PreviewMode::All => {
                jobs.push((preview_path(dir, "corrected"), true));
            }
        }
    }
    write_jobs_parallel(work, &jobs, space)
}

/// White-image crop used for FFC: manual rect, YOLO plane, or full frame.
fn ffc_plane_info(white: &RgbImage, crop_rect: Option<&str>) -> ([i32; 4], &'static str) {
    if let Some(s) = crop_rect {
        if let Some(r) = ccp_detect::plane::parse_crop_rect(s) {
            if r.width > 0 && r.height > 0 {
                return ([r.x, r.y, r.width, r.height], "crop_rect");
            }
        }
    }
    let rgb8: Vec<u8> = white
        .rgb
        .iter()
        .map(|v| (v.clamp(0.0, 1.0) * 255.0).round() as u8)
        .collect();
    let plane = ccp_detect::paths::plane_onnx_path();
    if let Some(path) = plane.as_deref() {
        if let Ok(Some(r)) = ccp_detect::plane::detect_plane(&rgb8, white.width, white.height, path)
        {
            if r.width > 0 && r.height > 0 {
                return ([r.x, r.y, r.width, r.height], "detected");
            }
        }
    }
    let full = detect_plane_or_full(&rgb8, white.width, white.height, None, None);
    (
        [full.x, full.y, full.width.max(1), full.height.max(1)],
        "full",
    )
}

fn attach_ffc_plane(report: &mut Value, saved: &SavedPipeline) {
    if let Some(rect) = saved.ffc_plane {
        report["ffc_plane"] = json!(rect);
    }
    if let Some(source) = &saved.ffc_plane_source {
        report["ffc_plane_source"] = json!(source);
    }
    if let Some(size) = saved.ffc_white_size {
        report["ffc_white_size"] = json!(size);
    }
}

fn ffc_method_name(method: FfcFitMethod) -> &'static str {
    match method {
        FfcFitMethod::Linear => "linear",
        FfcFitMethod::Poly => "poly",
        FfcFitMethod::Pls => "pls",
        FfcFitMethod::Nn => "nn",
    }
}

fn attach_process(report: &mut Value, saved: &SavedPipeline, req: &Value) {
    let ffc = ffc_params_from_req(req);
    let ffc_method = if saved.do_ffc {
        ffc_method_name(ffc.method)
    } else {
        "off"
    };
    report["do_ffc"] = json!(saved.do_ffc);
    report["do_gc"] = json!(saved.do_gc);
    report["do_wb"] = json!(saved.do_wb);
    report["do_cc"] = json!(saved.do_cc);
    report["ffc_method"] = json!(ffc_method);
    let parts = [
        if saved.do_ffc {
            format!("FFC {ffc_method}")
        } else {
            "FFC off".into()
        },
        if saved.do_gc {
            "GC safe".into()
        } else {
            "GC off".into()
        },
        if saved.do_wb {
            "WB".into()
        } else {
            "WB off".into()
        },
        if saved.do_cc {
            format!("CC {}", saved.kind)
        } else {
            "CC off".into()
        },
    ];
    report["process"] = json!(parts.join(" · "));
}

/// Fit + apply FFC → sat → GC → WB → CC. Stages mutate one f32 buffer.
pub fn run_from_json(req: &Value, progress: &mut dyn FnMut(&str)) -> Result<RunOutcome> {
    run_from_json_ex(req, progress, None)
}

pub fn run_from_json_ex(
    req: &Value,
    progress: &mut dyn FnMut(&str),
    ffc_cache: Option<Arc<FfcGainCache>>,
) -> Result<RunOutcome> {
    remember_apply_backend("cpu");
    let image_path = json_str(req, &["image", "input", "image_path"])
        .ok_or_else(|| Error::Message("run requires image".into()))?;
    let handshake = load_handshake_value(req)?;
    let crop = json_f64(req, &["crop"], 0.75);
    let face = json_i64(req, &["face"], 0).max(0) as usize;
    let use_all = json_bool(req, &["use_all", "chart_all"], false);
    let faces = parse_handshake(&handshake, face, use_all, crop)?;

    let white_path = json_str(req, &["white", "white_image", "white_image_path"]);
    let stages = StageConfig {
        do_sat_extrapolate: json_bool(req, &["do_sat", "do_sat_extrapolate"], true),
        do_gc: json_bool(req, &["do_gc"], true),
        do_wb: json_bool(req, &["do_wb"], true),
        do_cc: json_bool(req, &["do_cc"], true),
    };
    let do_ffc = json_bool(req, &["do_ffc"], white_path.is_some());
    if do_ffc && white_path.is_none_or(|p| p.is_empty()) {
        return Err(Error::Message("FFC requires a blank white-field image from the same capture setup; neither color chart is a flat-field reference.".into()));
    }
    let crop_rect = json_str(req, &["crop_rect"]).map(str::to_string);

    let model_name = json_str(req, &["model", "method"]).unwrap_or("rpcc2");
    let kind = parse_model_kind(model_name)?;
    let (input_space, input_white, target_space, target_white) = spaces_from_req(req);
    let settings = correction_settings(&input_space, &input_white, &target_space, &target_white)?;
    validate_stage_input(&settings, do_ffc, stages.do_gc, stages.do_wb)?;
    let mapping = mapping_from_req(req);
    let sampling = sampling_from_req(req);
    let neutrals = load_neutrals();

    let output = PathBuf::from(
        json_str(req, &["output", "output_path"])
            .ok_or_else(|| Error::Message("run requires output".into()))?,
    );
    let report = PathBuf::from(json_str(req, &["report"]).unwrap_or("report.json"));
    let model_out = PathBuf::from(json_str(req, &["model_out"]).unwrap_or("model.json"));
    let preview_dir = json_str(req, &["preview_dir"]).map(PathBuf::from);
    let lut_out = json_str(req, &["lut_out"]).map(PathBuf::from);
    let write_output = json_bool(req, &["write_output"], true);
    let preview_mode = PreviewMode::from_req(req);

    progress("Loading image");
    let honor_icc = settings.input_space.id == "srgb" && settings.input_space.white.id == "d65";
    let loaded = ccp_core::sample::load_image_with_icc(image_path, honor_icc)?;
    let (spec, before_patches, chart_type) = sample_faces(loaded.view(), &faces, sampling)?;
    let mut work = rgb_image_into_array3(loaded)?;
    let has_chart = !before_patches.is_empty();

    let mut stages_de = Vec::new();
    let mut prev_de = if has_chart {
        de_stats(&before_patches, true, &settings.input_space)
    } else {
        (f64::NAN, f64::NAN, f64::NAN)
    };

    let mut ffc_applied = false;
    let mut ffc_elapsed = 0.0;
    let mut ffc_plane = None;
    let mut ffc_plane_source = None;
    let mut ffc_white_size = None;
    let mut ffc_gain: Option<Arc<Array2<f32>>> = None;
    let ffc_params = ffc_params_from_req(req);
    if do_ffc {
        if let Some(white) = white_path {
            progress("FFC");
            let t = Instant::now();
            let ffc = resolve_cached_ffc(
                Path::new(white),
                crop_rect.as_deref(),
                &ffc_params,
                ffc_cache.as_ref(),
            )
            .map_err(|e| {
                Error::Message(format!(
                    "FFC reference could not be loaded or fitted: {e}. Provide a readable blank white-field image from the same capture setup."
                ))
            })?;
            ffc_plane = Some(ffc.plane);
            ffc_plane_source = Some(ffc.plane_source.clone());
            ffc_white_size = Some(ffc.white_size);
            ffc_gain = Some(Arc::clone(&ffc.gain));
            ffc_applied = true;
            ffc_elapsed = t.elapsed().as_secs_f64();
        }
    }
    let defer_fused_ffc_gc = ffc_applied
        && stages.do_gc
        && !preview_mode.wants_intermediate("ffc")
        && ffc_gain.is_some();
    let mut patches = if defer_fused_ffc_gc {
        sample_work_ffc(
            &work,
            &faces,
            sampling,
            ffc_gain.as_deref().expect("deferred FFC has gain"),
        )?
        .1
    } else {
        if let Some(gain) = ffc_gain.as_deref() {
            apply_ffc_inplace(&mut work, gain);
            if preview_mode.wants_intermediate("ffc") {
                maybe_preview(preview_dir.as_deref(), "ffc", &work)?;
            }
        }
        sample_work(&work, &faces, sampling)?.1
    };
    if has_chart && ffc_applied {
        let now = de_stats(&patches, false, &settings.input_space);
        stages_de.push(stage_json("FFC", prev_de, now, ffc_elapsed));
        prev_de = now;
    }
    if stages.do_sat_extrapolate {
        progress("Saturation");
        let _ = extrapolate_saturated(
            &mut patches,
            mapping.saturation_threshold,
            mapping.clip_lo,
            mapping.clip_hi,
        );
    }

    let mut saved = SavedPipeline {
        kind: kind.name().into(),
        do_ffc: ffc_applied,
        do_gc: stages.do_gc,
        do_wb: stages.do_wb,
        do_cc: stages.do_cc,
        input_space_id: input_space,
        input_white_id: input_white,
        target_space_id: target_space,
        target_white_id: target_white,
        ffc_plane,
        ffc_plane_source,
        ffc_white_size,
        ffc_params: ffc_applied.then_some(ffc_params),
        ..SavedPipeline::default()
    };
    saved.kind = kind.name().into();

    if stages.do_gc {
        progress("GC");
        let t = Instant::now();
        let gc = fit_gc(&spec, &patches, Some(&neutrals))?;
        if defer_fused_ffc_gc {
            apply_ffc_gc_fused_inplace(
                &mut work,
                ffc_gain.as_deref().expect("deferred FFC has gain"),
                &gc,
            );
        } else {
            apply_gc_inplace(&mut work, &gc)?;
        }
        saved.gc_model = Some(gc.clone());
        patches = sample_work(&work, &faces, sampling)?.1;
        let elapsed = t.elapsed().as_secs_f64();
        if preview_mode.wants_intermediate("gc") {
            maybe_preview(preview_dir.as_deref(), "gc", &work)?;
        }
        if has_chart {
            let now = de_stats(&patches, false, &settings.input_space);
            stages_de.push(stage_json("GC", prev_de, now, elapsed));
            prev_de = now;
        }
    }

    if stages.do_wb {
        progress("WB");
        let t = Instant::now();
        let wb = fit_wb(&spec, &patches, Some(&neutrals))?;
        apply_wb_inplace(&mut work, &wb);
        saved.wb_diag = Some(wb.diag);
        patches = sample_work(&work, &faces, sampling)?.1;
        let elapsed = t.elapsed().as_secs_f64();
        if preview_mode.wants_intermediate("wb") {
            maybe_preview(preview_dir.as_deref(), "wb", &work)?;
        }
        if has_chart {
            let now = de_stats(&patches, false, &settings.input_space);
            stages_de.push(stage_json("WB", prev_de, now, elapsed));
            prev_de = now;
        }
    }

    let mut feature_count = 0i32;
    if json_bool(req, &["skip_saturated"], false) {
        patches.retain(|p| !p.saturated);
    }

    let mut validation = None;
    let mut validation_elapsed = 0.0;
    let mut fit_warnings = Vec::new();
    if stages.do_cc && has_chart && json_bool(req, &["cross_validate"], true) {
        progress("Validating CC");
        let t = Instant::now();
        match cross_validate_correction(&patches, kind, &settings, RIDGE_LAMBDA, &mapping) {
            Ok(report) => validation = Some(report),
            Err(error) => fit_warnings.push(format!("Cross-validation unavailable: {error}")),
        }
        validation_elapsed = t.elapsed().as_secs_f64();
    }
    if kind == ModelKind::NeuralNet && stages.do_cc {
        if patches.len() < 64 && mapping.nn_batch_norm {
            fit_warnings.push(format!(
                "Batch normalization is high-variance with only {} chart patches; trust held-out ΔE, not the training fit.",
                patches.len()
            ));
        }
        if patches.len() < 64 && mapping.nn_dropout > 0.0 {
            fit_warnings.push(format!(
                "Dropout {:.0}% is usually unstable with only {} chart patches; compare held-out ΔE.",
                mapping.nn_dropout * 100.0,
                patches.len()
            ));
        }
        if mapping.hidden_layers.len() > 2 {
            fit_warnings.push(format!(
                "{} hidden layers are high-capacity for {} chart patches; a 64 or 100-unit single layer is the safer baseline.",
                mapping.hidden_layers.len(),
                patches.len()
            ));
        }
    }

    let mut cc_elapsed = 0.0;
    if stages.do_cc {
        progress("CC");
        let t = Instant::now();
        let model = fit_correction_model(&patches, kind, &settings, RIDGE_LAMBDA, &mapping)?;
        feature_count = model.feature_count;
        apply_cc_accel(&mut work, &model, &settings)?;
        saved.correction = Some(model);
        cc_elapsed = t.elapsed().as_secs_f64();
    }

    progress("Writing outputs");
    finish_image_writes(
        &work,
        &output,
        preview_dir.as_deref(),
        write_output,
        &preview_mode,
        if stages.do_cc {
            &settings.target_space
        } else {
            &settings.input_space
        },
    )?;
    if let Some(parent) = model_out.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(&model_out, serde_json::to_string_pretty(&saved)?)?;
    if json_bool(req, &["phase2_teacher"], true) {
        if let Ok(teacher) = emit_rpcc2_teacher(&patches, Some(&settings)) {
            let teacher_path = model_out.with_file_name("teacher_rpcc2.json");
            let _ = std::fs::write(teacher_path, serde_json::to_string_pretty(&teacher)?);
        }
    }

    if let Some(lut_path) = &lut_out {
        let (lut, _) = saved.lut_pipeline()?;
        write_cube(lut_path, "ColorCorrect Studio", DEFAULT_LUT_SIZE, &lut)?;
    }

    let after_patches = if has_chart {
        sample_work(&work, &faces, sampling)?.1
    } else {
        Vec::new()
    };
    let mut report_json = build_report(
        kind.name(),
        feature_count,
        &before_patches,
        &after_patches,
        has_chart,
        &settings.input_space,
        if stages.do_cc {
            &settings.target_space
        } else {
            &settings.input_space
        },
    );
    report_json["input_normalization"] = json!({
        "exif_orientation": "applied before detect, sample and export",
        "embedded_icc": if honor_icc { "convert to working sRGB when present" } else { "explicit pixel encoding; ICC conversion bypassed" },
    });
    if has_chart && stages.do_cc {
        let now = de_stats(&after_patches, false, &settings.target_space);
        stages_de.push(stage_json("CC", prev_de, now, cc_elapsed));
    }
    if has_chart && !stages_de.is_empty() {
        report_json["stages"] = Value::Array(stages_de);
    }
    if let Some(validation) = validation.as_ref() {
        report_json["validation_delta_e_2000"] = serde_json::to_value(validation)?;
        report_json["validation_scope"] = json!(
            "CC mapping only; GC/WB are fitted before folds. Not independent capture validation."
        );
        report_json["validation_elapsed_secs"] = json!(validation_elapsed);
        let training_mean = report_json["delta_e_2000"]["mean_after"]
            .as_f64()
            .unwrap_or(f64::NAN);
        if training_mean.is_finite() && validation.mean > training_mean * 2.0 {
            fit_warnings.push(format!(
                "Held-out ΔE {:.2} is much higher than training ΔE {:.2}; this fit is overfitting the chart.",
                validation.mean, training_mean
            ));
        } else if validation.mean > 10.0 {
            fit_warnings.push(format!(
                "High held-out mean ΔE {:.2}; verify the chart reference, patch order, exposure and color interpretation before reusing this model.", validation.mean));
        }
    }
    if let Some(stages) = report_json["stages"].as_array() {
        for stage in stages {
            let before = stage["mean_before"].as_f64().unwrap_or(0.0);
            let after = stage["mean_after"].as_f64().unwrap_or(0.0);
            if after > before + 0.25 {
                fit_warnings.push(format!(
                    "{} increased chart mean ΔE from {:.2} to {:.2}; compare this optional stage disabled. These scores depend on the selected chart reference.",
                    stage["name"].as_str().unwrap_or("Stage"), before, after));
            }
        }
    }
    if let Some(model) = saved
        .correction
        .as_ref()
        .filter(|model| model.kind == ModelKind::NeuralNet)
    {
        report_json["nn_diagnostics"] = json!({
            "epochs_trained": model.nn_epochs_trained,
            "best_internal_score": model.nn_best_validation_score,
            "best_internal_loss": model.nn_best_validation_loss,
            "outer_cross_validation_is_authoritative": true,
        });
    }
    if !fit_warnings.is_empty() {
        report_json["fit_warnings"] = json!(fit_warnings);
    }
    attach_ffc_plane(&mut report_json, &saved);
    attach_process(&mut report_json, &saved, req);
    if let Some(parent) = report.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(&report, serde_json::to_string_pretty(&report_json)?)?;

    Ok(RunOutcome {
        output,
        report,
        model_out,
        lut_out,
        preview_dir,
        has_delta_e: has_chart && report_json.get("metrics_hidden") == Some(&Value::Bool(false)),
        report_json,
        saved,
        chart_type,
    })
}

pub fn load_saved(path: impl AsRef<Path>) -> Result<SavedPipeline> {
    let text = std::fs::read_to_string(path)?;
    let v: Value = serde_json::from_str(&text)?;
    if v.get("correction").is_some()
        || v.get("gc_coeffs").is_some()
        || v.get("gc_model").is_some()
        || v.get("wb_diag").is_some()
    {
        return Ok(serde_json::from_value(v)?);
    }
    let correction: CorrectionModel = serde_json::from_value(v)?;
    Ok(SavedPipeline {
        version: 1,
        kind: correction.kind.name().into(),
        do_cc: true,
        correction: Some(correction),
        ..SavedPipeline::default()
    })
}

#[derive(Clone, Debug)]
pub struct ApplyOptions {
    pub do_ffc: bool,
    pub do_gc: bool,
    pub do_wb: bool,
    pub do_cc: bool,
    pub white: Option<PathBuf>,
    pub crop_rect: Option<String>,
    pub ffc: FfcFitParams,
    pub write_output: bool,
    pub preview_mode: PreviewMode,
    pub ffc_cache: Option<Arc<FfcGainCache>>,
}

impl Default for ApplyOptions {
    fn default() -> Self {
        Self {
            do_ffc: false,
            do_gc: true,
            do_wb: true,
            do_cc: true,
            white: None,
            crop_rect: None,
            ffc: FfcFitParams::default(),
            write_output: true,
            preview_mode: PreviewMode::None,
            ffc_cache: None,
        }
    }
}

impl ApplyOptions {
    pub fn from_saved(saved: &SavedPipeline) -> Self {
        Self {
            do_ffc: false,
            do_gc: saved.do_gc,
            do_wb: saved.do_wb,
            do_cc: saved.do_cc,
            white: None,
            crop_rect: None,
            ffc: saved.ffc_params.clone().unwrap_or_default(),
            write_output: true,
            preview_mode: PreviewMode::None,
            ffc_cache: None,
        }
    }

    pub fn from_req(req: &Value, saved: &SavedPipeline) -> Self {
        Self {
            do_ffc: json_bool(req, &["do_ffc"], false),
            do_gc: json_bool(req, &["do_gc"], saved.do_gc),
            do_wb: json_bool(req, &["do_wb"], saved.do_wb),
            do_cc: json_bool(req, &["do_cc"], saved.do_cc),
            white: json_str(req, &["white", "white_image", "white_image_path"])
                .filter(|s| !s.is_empty())
                .map(PathBuf::from),
            crop_rect: json_str(req, &["crop_rect"]).map(str::to_string),
            ffc: if req.get("ffc").is_some() || req.get("ffc_method").is_some() {
                ffc_params_from_req(req)
            } else {
                saved.ffc_params.clone().unwrap_or_default()
            },
            write_output: json_bool(req, &["write_output"], true),
            preview_mode: PreviewMode::from_req(req),
            ffc_cache: None,
        }
    }
}

/// Apply a saved GC+WB+CC model. No detect. No invented Classic-24 ΔE.
/// FFC is re-fit from the current white still when `do_ffc` is on — it is never in the cube.
pub fn compute_ffc_gain(
    white: &Path,
    crop_rect: Option<&str>,
    params: &FfcFitParams,
) -> Result<Array2<f32>> {
    Ok((*CachedFfc::compute(white, crop_rect, params)?.gain).clone())
}

pub fn apply_saved_to_image(saved: &SavedPipeline, image: &Path, output: &Path) -> Result<()> {
    apply_saved_to_image_ex(saved, image, output, None, &ApplyOptions::from_saved(saved))?;
    Ok(())
}

pub fn apply_saved_to_image_ex(
    saved: &SavedPipeline,
    image: &Path,
    output: &Path,
    preview_dir: Option<&Path>,
    apply: &ApplyOptions,
) -> Result<ApplyImageReport> {
    remember_apply_backend("cpu");
    let settings = correction_settings(
        &saved.input_space_id,
        &saved.input_white_id,
        &saved.target_space_id,
        &saved.target_white_id,
    )?;
    validate_stage_input(&settings, apply.do_ffc, apply.do_gc, apply.do_wb)?;
    let honor_icc = settings.input_space.id == "srgb" && settings.input_space.white.id == "d65";
    let loaded = ccp_core::sample::load_image_with_icc(image, honor_icc)?;
    let mut work = rgb_image_into_array3(loaded)?;
    let ffc_skipped = None;
    let gain = if apply.do_ffc {
        let white = apply
            .white
            .as_ref()
            .filter(|p| !p.as_os_str().is_empty())
            .ok_or_else(|| Error::Message("FFC requires a blank white-field image from the same capture setup; neither color chart is a flat-field reference.".into()))?;
        let ffc = resolve_cached_ffc(
            white,
            apply.crop_rect.as_deref(),
            &apply.ffc,
            apply.ffc_cache.as_ref(),
        )
        .map_err(|e| {
            Error::Message(format!(
                "FFC reference could not be loaded or fitted: {e}. Provide a readable blank white-field image from the same capture setup."
            ))
        })?;
        Some(Arc::clone(&ffc.gain))
    } else {
        None
    };

    let (lut, _) = saved.lut_pipeline()?;
    let can_fuse = apply.do_ffc
        && apply.do_gc
        && gain.is_some()
        && lut.gc.is_some()
        && !apply.preview_mode.wants_intermediate("ffc");
    if can_fuse {
        apply_ffc_gc_fused_inplace(
            &mut work,
            gain.as_ref().expect("fuse requires gain"),
            lut.gc.as_ref().expect("fuse requires gc"),
        );
    } else {
        if let Some(g) = &gain {
            apply_ffc_inplace(&mut work, g);
            if apply.preview_mode.wants_intermediate("ffc") {
                maybe_preview(preview_dir, "ffc", &work)?;
            }
        }
        if apply.do_gc {
            if let Some(gc) = &lut.gc {
                apply_gc_inplace(&mut work, gc)?;
                if apply.preview_mode.wants_intermediate("gc") {
                    maybe_preview(preview_dir, "gc", &work)?;
                }
            }
        }
    }
    if apply.do_wb {
        if let Some(wb) = &lut.wb {
            apply_wb_inplace(&mut work, wb);
            if apply.preview_mode.wants_intermediate("wb") {
                maybe_preview(preview_dir, "wb", &work)?;
            }
        }
    }
    if apply.do_cc {
        if let Some((model, settings)) = &lut.cc {
            apply_cc_accel(&mut work, model, settings)?;
        }
    }
    finish_image_writes(
        &work,
        output,
        preview_dir,
        apply.write_output,
        &apply.preview_mode,
        if apply.do_cc && lut.cc.is_some() {
            &settings.target_space
        } else {
            &settings.input_space
        },
    )?;
    Ok(ApplyImageReport { ffc_skipped })
}

pub fn export_cube_from_saved(
    saved: &SavedPipeline,
    path: impl AsRef<Path>,
    size: usize,
) -> Result<()> {
    let (lut, _) = saved.lut_pipeline()?;
    write_cube(path, "ColorCorrect Studio", size, &lut)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use ccp_core::ffc::{FfcFitMethod, FfcFitParams};
    use ccp_core::refs::chart_spec;
    use ccp_core::ChartKind;

    fn classic_handshake(width: u32, height: u32) -> Value {
        let cols = 6u32;
        let rows = 4u32;
        let cw = width as f64 / cols as f64;
        let ch = height as f64 / rows as f64;
        let mut obbs = Vec::new();
        for r in 0..rows {
            for c in 0..cols {
                let x0 = c as f64 * cw;
                let y0 = r as f64 * ch;
                let x1 = x0 + cw;
                let y1 = y0 + ch;
                obbs.push(json!([[x0, y0], [x1, y0], [x1, y1], [x0, y1]]));
            }
        }
        json!({
            "ok": true,
            "chart_type": "classic_4x6",
            "cli_chart": "classic",
            "patch_obbs": obbs,
        })
    }

    fn write_cast_classic(path: &Path, w: u32, h: u32) {
        let spec = chart_spec(ChartKind::Classic).unwrap();
        let cols = 6u32;
        let rows = 4u32;
        let cw = w / cols;
        let ch = h / rows;
        let mut img = Rgb8::new(w, h);
        for r in 0..rows {
            for c in 0..cols {
                let i = (r * cols + c) as usize;
                let rgb = spec.reference_srgb[i];
                let pix = image::Rgb([
                    (rgb[0] * 1.18 * 255.0).clamp(0.0, 255.0) as u8,
                    (rgb[1] * 0.88 * 255.0).clamp(0.0, 255.0) as u8,
                    (rgb[2] * 0.80 * 255.0).clamp(0.0, 255.0) as u8,
                ]);
                for y in (r * ch)..((r + 1) * ch) {
                    for x in (c * cw)..((c + 1) * cw) {
                        img.put_pixel(x, y, pix);
                    }
                }
            }
        }
        img.save(path).unwrap();
    }

    fn preview_exists(dir: &Path, stem: &str) -> bool {
        dir.join(format!("{stem}.rgb")).is_file() || dir.join(format!("{stem}.png")).is_file()
    }

    #[test]
    fn write_preview_emits_rgb_sidecar() {
        let dir = std::env::temp_dir().join(format!("ccp-preview-rgb-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let mut arr = Array3::<f32>::zeros((20, 30, 3));
        arr[[5, 7, 0]] = 1.0;
        write_preview(&arr, dir.join("corrected.png")).unwrap();
        let bytes = std::fs::read(dir.join("corrected.rgb")).unwrap();
        assert_eq!(&bytes[..8], b"CCPRGB8\n");
        assert_eq!(&bytes[8..12], 30u32.to_le_bytes());
        assert_eq!(&bytes[12..16], 20u32.to_le_bytes());
        flush_preview_encodes();
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn ffc_default_smooth_is_odd_five() {
        assert_eq!(FfcFitParams::default().smooth_window, 5);
        assert_eq!(FfcFitParams::default().smooth_window % 2, 1);
    }

    #[test]
    fn parse_handshake_reads_obbs() {
        let hs = classic_handshake(120, 80);
        let faces = parse_handshake(&hs, 0, false, 0.75).unwrap();
        assert_eq!(faces.len(), 1);
        assert_eq!(faces[0].patch_obbs.len(), 24);
        assert_eq!(faces[0].cli_chart, "classic");
    }

    #[test]
    fn ffc_request_without_white_fails_before_loading_the_image() {
        let request = json!({"image": "missing.jpg", "handshake": classic_handshake(120, 80),
            "output": "unused.png", "do_ffc": true});
        let error = run_from_json(&request, &mut |_| {})
            .err()
            .unwrap()
            .to_string();
        assert!(
            error.contains("FFC requires a blank white-field image"),
            "{error}"
        );
    }

    #[test]
    fn requested_ffc_fit_rejects_missing_unreadable_and_invalid_references() {
        let dir =
            std::env::temp_dir().join(format!("ccp-ffc-fit-rejection-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let invalid = dir.join("invalid-white.png");
        std::fs::write(&invalid, b"not a readable image").unwrap();
        for (index, white) in [dir.join("missing-white.png"), invalid, dir.clone()]
            .into_iter()
            .enumerate()
        {
            let output = dir.join(format!("output-{index}.png"));
            let report = dir.join(format!("report-{index}.json"));
            let model = dir.join(format!("model-{index}.json"));
            let request = json!({"image": image, "handshake": classic_handshake(120, 80),
                "output": output, "report": report, "model_out": model,
                "white": white, "do_ffc": true, "phase2_teacher": false});
            let error = run_from_json(&request, &mut |_| {})
                .err()
                .unwrap()
                .to_string();
            assert!(
                error.contains("FFC reference could not be loaded or fitted"),
                "{error}"
            );
            assert!(error.contains("blank white-field image"), "{error}");
            assert!(!output.exists() && !report.exists() && !model.exists());
        }
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn requested_ffc_apply_rejects_absent_missing_unreadable_and_invalid_references() {
        let dir =
            std::env::temp_dir().join(format!("ccp-ffc-apply-rejection-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let invalid = dir.join("invalid-white.png");
        std::fs::write(&invalid, b"not a readable image").unwrap();
        for (index, white) in [
            None,
            Some(PathBuf::new()),
            Some(dir.join("missing-white.png")),
            Some(invalid),
            Some(dir.clone()),
        ]
        .into_iter()
        .enumerate()
        {
            let output = dir.join(format!("output-{index}.png"));
            let apply = ApplyOptions {
                do_ffc: true,
                white,
                ..ApplyOptions::default()
            };
            let error =
                apply_saved_to_image_ex(&SavedPipeline::default(), &image, &output, None, &apply)
                    .err()
                    .unwrap()
                    .to_string();
            assert!(
                error.contains("FFC") && error.contains("blank white-field image"),
                "{error}"
            );
            assert!(!output.exists());
        }
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn disabled_ffc_ignores_unused_invalid_white_for_fit_and_apply() {
        let dir = std::env::temp_dir().join(format!("ccp-ffc-disabled-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let invalid = dir.join("invalid-white.png");
        std::fs::write(&invalid, b"not a readable image").unwrap();
        let request = json!({"image": image, "handshake": classic_handshake(120, 80),
            "output": dir.join("fitted.png"), "report": dir.join("report.json"),
            "model_out": dir.join("model.json"), "white": invalid,
            "do_ffc": false, "phase2_teacher": false});
        let fitted = run_from_json(&request, &mut |_| {}).unwrap();
        assert!(!fitted.saved.do_ffc);
        let output = dir.join("applied.png");
        let apply = ApplyOptions {
            do_ffc: false,
            white: Some(invalid),
            ..ApplyOptions::default()
        };
        let applied =
            apply_saved_to_image_ex(&fitted.saved, &image, &output, None, &apply).unwrap();
        assert!(output.is_file());
        assert!(applied.ffc_skipped.is_none());
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn nn_request_options_train_and_roundtrip_for_every_optimizer() {
        let dir =
            std::env::temp_dir().join(format!("ccp-bridge-nn-options-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        for optimizer in ["adam", "sgd", "rmsprop"] {
            let model_path = dir.join(format!("{optimizer}.json"));
            let req = json!({
                "image": image.to_string_lossy(),
                "handshake": classic_handshake(120, 80),
                "output": dir.join(format!("{optimizer}.png")).to_string_lossy(),
                "report": dir.join(format!("{optimizer}-report.json")).to_string_lossy(),
                "model_out": model_path.to_string_lossy(),
                "preview_mode": "none",
                "write_output": true,
                "do_ffc": false,
                "do_gc": false,
                "do_wb": false,
                "model": "nn",
                "hidden_layers": [16, 8],
                "nn_optimizer": optimizer,
                "nn_batch_norm": true,
                "nn_dropout": 0.2,
                "nn_lr": 0.005,
                "nn_epochs": 80,
                "nn_patience": 20,
            });
            let outcome = run_from_json(&req, &mut |_| {}).unwrap();
            assert!(outcome.output.is_file());
            let saved = load_saved(&model_path).unwrap();
            let model = saved.correction.expect("NN correction model");
            assert_eq!(model.hidden_layers, vec![16, 8]);
            assert_eq!(model.nn_optimizer, optimizer);
            assert!(model.nn_batch_norm);
            assert!(model.nn_residual);
            assert!((model.nn_dropout - 0.2).abs() < 1e-12);
            assert_eq!(model.layer_weights.len(), 3);
            let corrected = model.apply_linear([0.4, 0.3, 0.2]).unwrap();
            assert!(corrected.iter().all(|value| value.is_finite()));
            let report: Value = serde_json::from_slice(
                &std::fs::read(dir.join(format!("{optimizer}-report.json"))).unwrap(),
            )
            .unwrap();
            let before = report["delta_e_2000"]["mean_before"].as_f64().unwrap();
            let after = report["delta_e_2000"]["mean_after"].as_f64().unwrap();
            assert!(
                after <= before,
                "{optimizer} regressed Delta E {before} -> {after}"
            );
        }
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn run_fit_writes_outputs_and_honest_de() {
        let dir = std::env::temp_dir().join(format!("ccp-bridge-run-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let handshake = classic_handshake(120, 80);
        let req = json!({
            "image": image.to_string_lossy(),
            "handshake": handshake,
            "output": dir.join("out.png").to_string_lossy(),
            "report": dir.join("report.json").to_string_lossy(),
            "model_out": dir.join("model.json").to_string_lossy(),
            "preview_dir": dir.join("prev").to_string_lossy(),
            "lut_out": dir.join("shot.cube").to_string_lossy(),
            "model": "rpcc2",
            "do_ffc": false,
            "do_sat": true,
            "do_gc": true,
            "do_wb": true,
            "do_cc": true,
            "fit_samples": 5,
        });
        let mut notes = Vec::new();
        let out = run_from_json(&req, &mut |m| notes.push(m.to_string())).unwrap();
        assert!(out.output.is_file());
        assert!(out.report.is_file());
        assert!(out.model_out.is_file());
        assert!(dir.join("shot.cube").is_file());
        assert!(
            preview_exists(&dir.join("prev"), "corrected"),
            "missing corrected preview"
        );
        for name in ["original", "gc", "wb", "cc"] {
            assert!(
                !preview_exists(&dir.join("prev"), name),
                "lazy apply must not write {name} preview"
            );
        }
        assert!(
            !preview_exists(&dir.join("prev"), "ffc"),
            "FFC preview must be skipped when FFC did not run"
        );
        assert_eq!(out.report_json["do_ffc"], false);
        assert_eq!(out.report_json["do_gc"], true);
        assert_eq!(out.report_json["do_wb"], true);
        assert_eq!(out.report_json["do_cc"], true);
        assert_eq!(out.report_json["ffc_method"], "off");
        assert!(
            out.report_json["process"]
                .as_str()
                .unwrap_or("")
                .contains("FFC off · GC safe · WB · CC"),
            "report must record the UI step config: {}",
            out.report_json["process"]
        );
        assert!(out.has_delta_e, "chart fit must expose honest ΔE");
        let de = &out.report_json["delta_e_2000"];
        assert!(de["mean_before"].as_f64().unwrap().is_finite());
        assert!(de["mean_after"].as_f64().unwrap().is_finite());
        let stages = out.report_json["stages"]
            .as_array()
            .expect("chart fit must report per-stage ΔE from existing handshake samples");
        let names: Vec<&str> = stages
            .iter()
            .map(|s| s["name"].as_str().unwrap_or(""))
            .collect();
        assert_eq!(
            names,
            vec!["GC", "WB", "CC"],
            "FFC off must omit FFC: {names:?}"
        );
        for stage in stages {
            assert!(stage["mean_before"].as_f64().unwrap().is_finite());
            assert!(stage["mean_after"].as_f64().unwrap().is_finite());
        }
        let last_after = stages.last().unwrap()["mean_after"].as_f64().unwrap();
        let overall_after = de["mean_after"].as_f64().unwrap();
        assert!(
            (last_after - overall_after).abs() < 1e-9,
            "CC after must match overall mean_after: {last_after} vs {overall_after}"
        );
        let cube = std::fs::read_to_string(dir.join("shot.cube")).unwrap();
        assert!(cube.contains("LUT does not include flat-field"));
        assert!(!notes.is_empty());
    }

    #[test]
    fn corrected_preview_converts_to_srgb_without_changing_export_pixels() {
        let dir = std::env::temp_dir().join(format!("ccp-color-preview-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let white = parse_white_point("d65").unwrap();
        let linear = parse_rgb_color_space("linear-srgb", &white).unwrap();
        let work = Array3::from_elem((2, 2, 3), 0.21404114f32);
        finish_image_writes(
            &work,
            &dir.join("linear.png"),
            Some(&dir),
            true,
            &PreviewMode::Corrected,
            &linear,
        )
        .unwrap();
        let exported = image::open(dir.join("linear.png")).unwrap().to_rgb8();
        assert_eq!(exported.get_pixel(0, 0).0, [55; 3]);
        let sidecar = std::fs::read(dir.join("corrected.rgb")).unwrap();
        assert_eq!(&sidecar[sidecar.len() - 3..], &[128; 3]);
        join_pending_preview_png(&dir.join("corrected.png"));
        let preview = image::open(dir.join("corrected.png")).unwrap().to_rgb8();
        assert_eq!(preview.get_pixel(0, 0).0, [128; 3]);
        assert_eq!(work[[0, 0, 0]], 0.21404114);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn reports_use_declared_input_and_output_spaces() {
        use ccp_core::color::{lab_d50_to_target_linear, linear_to_encoded_rgb, Lab};
        let spec = chart_spec_from_name("classic").unwrap();
        let sample = Lab::new(60.0, 5.0, -5.0);
        let base = ChartPatch {
            name: spec.names[0].clone(),
            index: 0,
            reference_lab_d50: sample,
            reference_srgb: ccp_core::lab_d50_to_srgb(sample).unwrap(),
            measured_srgb: [0.0; 3],
            original_measured_srgb: [0.0; 3],
            center: Vec2 { x: 0.0, y: 0.0 },
            saturated: false,
        };
        for input in ccp_core::color::supported_rgb_color_spaces() {
            for output in ccp_core::color::supported_rgb_color_spaces() {
                let native_white = |name: &str| match name {
                    "prophoto-rgb" => "d50",
                    "acescg" => "d60",
                    _ => "d65",
                };
                let settings =
                    correction_settings(input, native_white(input), output, native_white(output))
                        .unwrap();
                let encode = |space: &RgbColorSpace| {
                    linear_to_encoded_rgb(
                        lab_d50_to_target_linear(sample, space, &space.white).unwrap(),
                        space,
                    )
                };
                let mut before = base.clone();
                before.original_measured_srgb = encode(&settings.input_space);
                before.measured_srgb = before.original_measured_srgb;
                let mut after = base.clone();
                after.measured_srgb = encode(&settings.target_space);
                let raw_before = before.original_measured_srgb;
                let raw_after = after.measured_srgb;
                let report = build_report(
                    "linear",
                    3,
                    &[before],
                    &[after],
                    true,
                    &settings.input_space,
                    &settings.target_space,
                );
                assert!(
                    report["delta_e_2000"]["mean_before"].as_f64().unwrap() < 1e-10,
                    "{input}->{output}"
                );
                assert!(
                    report["delta_e_2000"]["mean_after"].as_f64().unwrap() < 1e-10,
                    "{input}->{output}"
                );
                assert_eq!(
                    report["colorimetry"]["output_space"],
                    settings.target_space.id
                );
                let patch = &report["patches"][0];
                // Preserve raw numeric fields for API consumers and old reports.
                assert_eq!(patch["measured_srgb"], json!(raw_before));
                assert_eq!(patch["corrected_srgb"], json!(raw_after));
                for field in [
                    "measured_display_srgb",
                    "reference_display_srgb",
                    "corrected_display_srgb",
                ] {
                    let display = patch[field].as_array().unwrap();
                    for (channel, value) in display.iter().enumerate() {
                        assert!(
                            (value.as_f64().unwrap() - base.reference_srgb[channel]).abs() < 1e-10,
                            "{input}->{output}: {field} must represent the same display color"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn non_srgb_stages_fail_closed_but_cc_only_is_supported() {
        for name in ccp_core::color::supported_rgb_color_spaces() {
            for white in ccp_core::color::supported_white_points() {
                let settings = correction_settings(name, white, "srgb", "d65").unwrap();
                assert!(validate_stage_input(&settings, false, false, false).is_ok());
                let expected = name == &"srgb" && white == &"d65";
                for flags in [
                    (true, false, false),
                    (false, true, false),
                    (false, false, true),
                ] {
                    assert_eq!(
                        validate_stage_input(&settings, flags.0, flags.1, flags.2).is_ok(),
                        expected
                    );
                }
            }
        }
    }

    #[test]
    fn apply_hides_invented_delta_e() {
        let dir = std::env::temp_dir().join(format!("ccp-bridge-apply-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let image = dir.join("chart.png");
        write_cast_classic(&image, 120, 80);
        let req = json!({
            "image": image.to_string_lossy(),
            "handshake": classic_handshake(120, 80),
            "output": dir.join("fit.png").to_string_lossy(),
            "report": dir.join("fit_report.json").to_string_lossy(),
            "model_out": dir.join("model.json").to_string_lossy(),
            "do_ffc": false,
            "model": "rpcc2",
        });
        let out = run_from_json(&req, &mut |_| {}).unwrap();
        let saved = load_saved(&out.model_out).unwrap();
        let applied = dir.join("applied.png");
        apply_saved_to_image(&saved, &image, &applied).unwrap();
        assert!(applied.is_file());
        let settings = CorrectionSettings::srgb_d65().unwrap();
        let hidden = build_report(
            "rpcc2",
            0,
            &[],
            &[],
            false,
            &settings.input_space,
            &settings.target_space,
        );
        assert_eq!(hidden["metrics_hidden"], true);
        assert!(hidden.get("delta_e_2000").is_none());
        assert!(
            hidden.get("stages").is_none(),
            "apply-only must not invent stage ΔE"
        );

        let prev = dir.join("apply_prev");
        std::fs::create_dir_all(&prev).unwrap();
        let gated = dir.join("gated.png");
        let apply = ApplyOptions {
            do_ffc: true,
            do_gc: false,
            do_wb: true,
            do_cc: true,
            white: Some(image.clone()),
            crop_rect: None,
            ffc: FfcFitParams {
                method: FfcFitMethod::Linear,
                ..FfcFitParams::default()
            },
            write_output: true,
            preview_mode: PreviewMode::All,
            ffc_cache: None,
        };
        apply_saved_to_image_ex(&saved, &image, &gated, Some(&prev), &apply).unwrap();
        assert!(gated.is_file());
        assert!(
            preview_exists(&prev, "ffc"),
            "apply-model must re-run FFC when enabled"
        );
        assert!(
            !preview_exists(&prev, "gc"),
            "apply-model must honor GC off"
        );
        assert!(preview_exists(&prev, "wb"));
        assert!(preview_exists(&prev, "corrected"));
    }

    #[test]
    fn saved_pipeline_roundtrips_nn_pls_linear() {
        for kind in [
            ModelKind::NeuralNet,
            ModelKind::Pls,
            ModelKind::Linear,
            ModelKind::Ccm3x3,
            ModelKind::RootPolynomial2,
        ] {
            let mut model = CorrectionModel {
                kind,
                feature_count: 8,
                coefficients: vec![0.1; 24],
                x_mean: vec![0.0; 8],
                x_std: vec![1.0; 8],
                ..CorrectionModel::default()
            };
            if kind == ModelKind::NeuralNet {
                model.hidden_size = 8;
                model.w1 = vec![0.01; 64];
                model.b1 = vec![0.0; 8];
                model.w2 = vec![0.02; 24];
                model.b2 = vec![0.0; 3];
            }
            if kind == ModelKind::Pls {
                model.pls_components = 4;
            }
            let saved = SavedPipeline {
                kind: kind.name().into(),
                correction: Some(model),
                do_cc: true,
                ffc_plane: Some([10, 20, 400, 300]),
                ffc_plane_source: Some("detected".into()),
                ffc_white_size: Some([800, 600]),
                ffc_params: Some(FfcFitParams {
                    method: FfcFitMethod::Poly,
                    degree: 5,
                    ..FfcFitParams::default()
                }),
                ..SavedPipeline::default()
            };
            let json = serde_json::to_string_pretty(&saved).unwrap();
            assert!(
                json.contains(&format!("\"kind\": \"{}\"", kind.name()))
                    || json.contains(&kind.name().to_string())
            );
            let back: SavedPipeline = serde_json::from_str(&json).unwrap();
            assert_eq!(back.kind, kind.name());
            let c = back.correction.expect("correction");
            assert_eq!(c.kind, kind);
            assert_eq!(c.coefficients.len(), 24);
            if kind == ModelKind::NeuralNet {
                assert_eq!(c.w1.len(), 64);
                assert_eq!(c.w2.len(), 24);
            }
            assert_eq!(back.ffc_plane, Some([10, 20, 400, 300]));
            assert_eq!(back.ffc_plane_source.as_deref(), Some("detected"));
            assert_eq!(
                back.ffc_params.as_ref().map(|p| p.method),
                Some(FfcFitMethod::Poly)
            );
        }

        let nn = CorrectionModel {
            kind: ModelKind::NeuralNet,
            hidden_size: 4,
            w1: vec![0.2; 16],
            b1: vec![0.0; 4],
            w2: vec![0.1; 12],
            b2: vec![0.0; 3],
            ..CorrectionModel::default()
        };
        let dir = std::env::temp_dir().join(format!("ccp-nn-bare-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        let path = dir.join("nn.json");
        std::fs::write(&path, serde_json::to_string(&nn).unwrap()).unwrap();
        let loaded = load_saved(&path).unwrap();
        assert_eq!(loaded.kind, "nn");
        assert_eq!(loaded.correction.unwrap().w1.len(), 16);

        let legacy = SavedPipeline {
            version: 1,
            do_gc: true,
            gc_coeffs: Some(vec![1.05, -2.0]),
            gc_model: None,
            ..SavedPipeline::default()
        };
        let legacy_json = serde_json::to_string(&legacy).unwrap();
        let legacy_back: SavedPipeline = serde_json::from_str(&legacy_json).unwrap();
        let (legacy_lut, _) = legacy_back.lut_pipeline().unwrap();
        assert!((legacy_lut.gc.unwrap().map_lstar(50.0) - 50.5).abs() < 1e-12);
    }

    #[test]
    fn ffc_cache_reuses_same_white() {
        let dir = std::env::temp_dir().join(format!("ccp-ffc-cache-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        let image = dir.join("white.png");
        write_cast_classic(&image, 40, 30);
        let params = FfcFitParams {
            method: FfcFitMethod::Linear,
            bins: 8,
            ..FfcFitParams::default()
        };
        let cache = FfcGainCache::new();
        let a = cache.get_or_compute(&image, None, &params).unwrap();
        let b = cache.get_or_compute(&image, None, &params).unwrap();
        assert!(Arc::ptr_eq(&a, &b));
        assert!(Arc::ptr_eq(&a.gain, &b.gain));
        assert_eq!(a.gain, b.gain);
        assert_eq!(a.plane, b.plane);
    }
}
