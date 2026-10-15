//! IRIDAS `.cube` — GC + WB + CC only. Flat-field is a separate 2D gain map.

use crate::color::Vec3;
use crate::maps::{apply_correction_pixel, CorrectionModel, CorrectionSettings};
use crate::stages::{apply_gc_pixel, apply_wb_pixel, GammaModel, WhiteBalance};
use crate::{Error, Result};
use ndarray::Array2;
use std::io::Write;
use std::path::Path;

pub const DEFAULT_LUT_SIZE: usize = 33;
pub const LUT_NO_FFC_COMMENT: &str = "LUT does not include flat-field.";

#[derive(Clone, Debug, Default)]
pub struct LutPipeline {
    pub gc: Option<GammaModel>,
    pub wb: Option<WhiteBalance>,
    pub cc: Option<(CorrectionModel, CorrectionSettings)>,
}

impl LutPipeline {
    pub fn apply_color(&self, mut rgb: Vec3) -> Result<Vec3> {
        if let Some(gc) = &self.gc {
            rgb = apply_gc_pixel(rgb, gc)?;
        }
        if let Some(wb) = &self.wb {
            rgb = apply_wb_pixel(rgb, wb);
        }
        if let Some((model, settings)) = &self.cc {
            rgb = apply_correction_pixel(model, rgb, settings)?;
        }
        Ok(rgb)
    }
}

/// IRIDAS 3D LUT text. Red varies fastest, then green, then blue.
pub fn cube_text(title: &str, size: usize, pipeline: &LutPipeline) -> Result<String> {
    let size = if size == 0 { DEFAULT_LUT_SIZE } else { size };
    let mut out = String::new();
    out.push_str(&format!("TITLE \"{title}\"\n"));
    out.push_str(&format!("# {LUT_NO_FFC_COMMENT}\n"));
    out.push_str("# Contents: GC + WB + CC only. Export the gain map separately.\n");
    out.push_str("DOMAIN_MIN 0.0 0.0 0.0\n");
    out.push_str("DOMAIN_MAX 1.0 1.0 1.0\n");
    out.push_str(&format!("LUT_3D_SIZE {size}\n\n"));
    let denom = (size - 1).max(1) as f64;
    for bi in 0..size {
        for gi in 0..size {
            for ri in 0..size {
                let rgb = [ri as f64 / denom, gi as f64 / denom, bi as f64 / denom];
                let c = pipeline.apply_color(rgb)?;
                out.push_str(&format!("{:.8} {:.8} {:.8}\n", c[0], c[1], c[2]));
            }
        }
    }
    Ok(out)
}

pub fn write_cube(
    path: impl AsRef<Path>,
    title: &str,
    size: usize,
    pipeline: &LutPipeline,
) -> Result<()> {
    let text = cube_text(title, size, pipeline)?;
    std::fs::write(path, text)?;
    Ok(())
}

fn write_f32_tiff(path: &Path, gain: &Array2<f32>) -> Result<()> {
    let h = gain.nrows() as u32;
    let w = gain.ncols() as u32;
    let mut pixels = Vec::with_capacity(gain.len() * 4);
    for v in gain.iter() {
        pixels.extend_from_slice(&v.to_le_bytes());
    }
    let ifd_offset = 8u32;
    let n_entries = 11u16;
    let data_offset = ifd_offset + 2 + u32::from(n_entries) * 12 + 4;
    let mut buf = Vec::new();
    buf.extend_from_slice(b"II");
    buf.extend_from_slice(&42u16.to_le_bytes());
    buf.extend_from_slice(&ifd_offset.to_le_bytes());
    buf.extend_from_slice(&n_entries.to_le_bytes());

    let mut write_entry = |tag: u16, typ: u16, count: u32, value: u32| {
        buf.extend_from_slice(&tag.to_le_bytes());
        buf.extend_from_slice(&typ.to_le_bytes());
        buf.extend_from_slice(&count.to_le_bytes());
        buf.extend_from_slice(&value.to_le_bytes());
    };
    write_entry(256, 4, 1, w);
    write_entry(257, 4, 1, h);
    write_entry(258, 3, 1, 32);
    write_entry(259, 3, 1, 1);
    write_entry(262, 3, 1, 1);
    write_entry(273, 4, 1, data_offset);
    write_entry(277, 3, 1, 1);
    write_entry(278, 4, 1, h);
    write_entry(279, 4, 1, pixels.len() as u32);
    write_entry(339, 3, 1, 3);
    write_entry(284, 3, 1, 1);
    buf.extend_from_slice(&0u32.to_le_bytes());
    buf.extend_from_slice(&pixels);
    std::fs::write(path, buf)?;
    Ok(())
}

fn write_f32_exr(path: &Path, gain: &Array2<f32>) -> Result<()> {
    let mut rgb = Vec::with_capacity(gain.len() * 3);
    for v in gain.iter() {
        rgb.extend_from_slice(&[*v, *v, *v]);
    }
    let buf = image::ImageBuffer::<image::Rgb<f32>, _>::from_raw(
        gain.ncols() as u32,
        gain.nrows() as u32,
        rgb,
    )
    .ok_or_else(|| Error::Runtime("gain map is not a packed RGB f32 buffer".into()))?;
    buf.save_with_format(path, image::ImageFormat::OpenExr)?;
    Ok(())
}

/// Separate optional f32 TIFF/EXR export. Never bake this into a `.cube`.
pub fn export_gain_map(path: impl AsRef<Path>, gain: &Array2<f32>) -> Result<()> {
    let path = path.as_ref();
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("tif")
        .to_ascii_lowercase();
    match ext.as_str() {
        "exr" => write_f32_exr(path, gain),
        _ => write_f32_tiff(path, gain),
    }
}

pub fn write_gain_comment_note<W: Write>(mut w: W) -> Result<()> {
    writeln!(w, "{LUT_NO_FFC_COMMENT}")?;
    Ok(())
}
