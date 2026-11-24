//! Fixtures shared by the worker's tests: synthetic chart images and handshakes.

use std::path::Path;

use ccp_core::refs::{chart_spec, ChartKind};
use image::RgbImage as Rgb8;
use serde_json::{json, Value};

use crate::error::Result;
use crate::ffc::FfcGainCache;
use crate::fit::{run, RunOutcome};

pub(crate) fn run_from_json(req: &Value, progress: &mut dyn FnMut(&str)) -> Result<RunOutcome> {
    run(req, progress, &FfcGainCache::default())
}

pub(crate) fn classic_handshake(width: u32, height: u32) -> Value {
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

pub(crate) fn write_cast_classic(path: &Path, w: u32, h: u32) {
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

pub(crate) fn preview_exists(dir: &Path, stem: &str) -> bool {
    dir.join(format!("{stem}.rgb")).is_file() || dir.join(format!("{stem}.png")).is_file()
}
