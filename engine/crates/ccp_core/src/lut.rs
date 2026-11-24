//! IRIDAS `.cube` — GC + WB + CC only. Flat-field is a separate 2D gain map.

use crate::color::Vec3;
use crate::maps::{apply_correction_pixel, CorrectionModel, CorrectionSettings};
use crate::stages::{apply_gc_pixel, apply_wb_pixel, GammaModel, WhiteBalance};
use crate::Result;
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
