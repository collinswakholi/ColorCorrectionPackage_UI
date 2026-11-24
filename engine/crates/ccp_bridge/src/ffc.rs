//! Flat-field correction gain maps fitted from a blank white-field image.
//!
//! The gain is spatial, so it is refitted for every session from the white
//! image and never stored in a saved model or LUT. The worker keeps the last
//! fit so a batch with one white image fits it once.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::SystemTime;

use ccp_core::ffc::{compute_multiplier, FfcFitParams};
use ccp_core::sample::{load_image, RgbImage};
use ccp_detect::plane::{detect_plane, detect_plane_or_full, parse_crop_rect};
use ndarray::Array2;

use crate::error::{Error, ErrorCode, Result};
use crate::work;

/// The request enables FFC but names no white-field image.
pub(crate) const MISSING_WHITE: &str = "FFC requires a blank white-field image from the same capture setup; neither color chart is a flat-field reference.";

/// A failed FFC fit on a valid request is a processing failure, never a
/// request error; other codes (I/O, decode, inference) are kept.
pub(crate) fn failure(error: &Error, message: String) -> Error {
    let code = match error.code() {
        ErrorCode::InvalidRequest => ErrorCode::ProcessingFailed,
        code => code,
    };
    Error::coded(code, message)
}

/// A fitted gain map and the white-image region it was fitted on.
#[derive(Debug)]
pub(crate) struct WhiteField {
    gain: Arc<Array2<f32>>,
    plane: [i32; 4],
    plane_source: &'static str,
    white_size: [i32; 2],
}

impl WhiteField {
    fn fit(white: &Path, crop: Option<&str>, params: &FfcFitParams) -> Result<Self> {
        let white_img = load_image(white).map_err(|error| {
            Error::image_load(&error, format!("Cannot load FFC white image {}: {error}. Provide a readable blank white-field image from the same capture setup.", white.display()))
        })?;
        let (plane, plane_source) = plane(&white_img, crop)?;
        let white_size = [white_img.width as i32, white_img.height as i32];
        let white_arr = work::from_image(white_img)?;
        let gain = compute_multiplier(Some(&white_arr), Some(plane), params)?;
        Ok(Self {
            gain: Arc::new(gain),
            plane,
            plane_source,
            white_size,
        })
    }

    pub(crate) fn gain(&self) -> &Array2<f32> {
        &self.gain
    }

    /// `[x, y, width, height]` of the fitted white region.
    pub(crate) fn plane(&self) -> [i32; 4] {
        self.plane
    }

    /// `crop_rect`, `detected` or `full`.
    pub(crate) fn plane_source(&self) -> &'static str {
        self.plane_source
    }

    pub(crate) fn white_size(&self) -> [i32; 2] {
        self.white_size
    }
}

/// White-image crop used for FFC: manual rect, YOLO plane, or full frame.
fn plane(white: &RgbImage, crop_rect: Option<&str>) -> Result<([i32; 4], &'static str)> {
    if let Some(s) = crop_rect {
        let r = parse_crop_rect(s).ok_or_else(|| {
            Error::Message("Invalid FFC crop: use x0,y0,x1,y1 coordinates.".into())
        })?;
        if r.width <= 0
            || r.height <= 0
            || r.x < 0
            || r.y < 0
            || i64::from(r.x) + i64::from(r.width) > i64::from(white.width)
            || i64::from(r.y) + i64::from(r.height) > i64::from(white.height)
        {
            return Err(Error::Message(
                "FFC crop must have positive dimensions and lie inside the white image.".into(),
            ));
        }
        return Ok(([r.x, r.y, r.width, r.height], "crop_rect"));
    }
    let rgb8: Vec<u8> = white
        .rgb
        .iter()
        .map(|v| (v.clamp(0.0, 1.0) * 255.0).round() as u8)
        .collect();
    let model = ccp_detect::paths::plane_onnx_path().ok_or_else(|| {
        Error::coded(
            ErrorCode::InferenceFailed,
            "White-plane detection model is missing. Repair the application setup or provide a manual FFC crop.",
        )
    })?;
    let detected = detect_plane(&rgb8, white.width, white.height, &model).map_err(|e| {
        Error::coded(
            ErrorCode::InferenceFailed,
            format!("White-plane detection failed: {e}. Check the detection engine or provide a manual FFC crop."),
        )
    })?;
    if let Some(r) = detected.filter(|r| r.width > 0 && r.height > 0) {
        return Ok(([r.x, r.y, r.width, r.height], "detected"));
    }
    let full = detect_plane_or_full(&rgb8, white.width, white.height, None, None);
    Ok((
        [full.x, full.y, full.width.max(1), full.height.max(1)],
        "full",
    ))
}

/// Everything a fitted gain depends on: the white file's identity and contents
/// stamp, the crop, and every fit parameter.
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

/// The most recent white-field fit of a worker.
#[derive(Debug, Default)]
pub(crate) struct FfcGainCache {
    last: Mutex<Option<(FfcGainKey, Arc<WhiteField>)>>,
}

impl FfcGainCache {
    pub(crate) fn get_or_fit(
        &self,
        white: &Path,
        crop: Option<&str>,
        params: &FfcFitParams,
    ) -> Result<Arc<WhiteField>> {
        let key = FfcGainKey::new(white, crop, params);
        let cached = |last: &Option<(FfcGainKey, Arc<WhiteField>)>| {
            last.as_ref()
                .filter(|(k, _)| k == &key)
                .map(|(_, v)| Arc::clone(v))
        };
        if let Some(hit) = cached(&self.last.lock().unwrap_or_else(|e| e.into_inner())) {
            return Ok(hit);
        }
        let fitted = Arc::new(WhiteField::fit(white, crop, params)?);
        let mut last = self.last.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(hit) = cached(&last) {
            return Ok(hit);
        }
        *last = Some((key, Arc::clone(&fitted)));
        Ok(fitted)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ccp_core::ffc::FfcFitMethod;

    #[test]
    fn ffc_cache_reuses_same_white() {
        let dir = std::env::temp_dir().join(format!("ccp-ffc-cache-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        let image = dir.join("white.png");
        crate::fit::tests::write_cast_classic(&image, 40, 30);
        let params = FfcFitParams {
            method: FfcFitMethod::Linear,
            bins: 8,
            ..FfcFitParams::default()
        };
        let cache = FfcGainCache::default();
        let a = cache
            .get_or_fit(&image, Some("0,0,40,30"), &params)
            .unwrap();
        let b = cache
            .get_or_fit(&image, Some("0,0,40,30"), &params)
            .unwrap();
        assert!(Arc::ptr_eq(&a, &b));
        assert!(Arc::ptr_eq(&a.gain, &b.gain));
        assert_eq!(a.gain, b.gain);
        assert_eq!(a.plane, b.plane);
    }
}
