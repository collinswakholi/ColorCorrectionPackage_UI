//! The f32 working image every stage mutates in place: `[height, width, rgb]`.

use std::borrow::Cow;
use std::path::Path;

use ccp_core::color::RgbColorSpace;
use ccp_core::sample::{load_image_with_icc, RgbImage};
use ndarray::Array3;

use crate::error::{Error, Result};

/// GC, WB and FFC operate on encoded sRGB/D65, which is also the only input
/// selection that honours an embedded ICC profile.
pub(crate) fn is_srgb_d65(space: &RgbColorSpace) -> bool {
    space.id == "srgb" && space.white.id == "d65"
}

/// Decode an input image, applying EXIF orientation and, for sRGB/D65 input,
/// its ICC profile. Other selections describe the stored pixels as-is.
pub(crate) fn load(path: &Path, space: &RgbColorSpace) -> Result<RgbImage> {
    load_image_with_icc(path, is_srgb_d65(space)).map_err(|e| Error::image_load(&e, e.to_string()))
}

pub(crate) fn from_image(img: RgbImage) -> Result<Array3<f32>> {
    Array3::from_shape_vec((img.height as usize, img.width as usize, 3), img.rgb)
        .map_err(|e| Error::Message(e.to_string()))
}

/// Row-major samples, copied only when the array is not already contiguous.
pub(crate) fn samples(arr: &Array3<f32>) -> Cow<'_, [f32]> {
    match arr.as_slice() {
        Some(slice) => Cow::Borrowed(slice),
        None => Cow::Owned(arr.iter().copied().collect()),
    }
}

/// Re-lay a transposed or sliced array out contiguously, keeping its shape.
pub(crate) fn make_contiguous(arr: &mut Array3<f32>) -> Result<()> {
    if arr.as_slice().is_none() {
        let dim = arr.raw_dim();
        let owned: Vec<f32> = arr.iter().copied().collect();
        *arr = Array3::from_shape_vec(dim, owned).map_err(|e| Error::Message(e.to_string()))?;
    }
    Ok(())
}
