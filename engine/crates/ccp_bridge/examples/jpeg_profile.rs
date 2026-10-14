//! Focused full-resolution JPEG encoder benchmark. Not a product command.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

use image::codecs::jpeg::JpegEncoder;
use image::{ExtendedColorType, ImageReader};
use jpeg_encoder::{ColorType, Encoder, SamplingFactor};
use serde_json::json;

fn repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("repo root")
        .to_path_buf()
}

fn timed(mut f: impl FnMut()) -> Vec<f64> {
    f();
    (0..3)
        .map(|_| {
            let started = Instant::now();
            f();
            started.elapsed().as_secs_f64() * 1000.0
        })
        .collect()
}

fn p50(values: &[f64]) -> f64 {
    let mut sorted = values.to_vec();
    sorted.sort_by(|a, b| a.total_cmp(b));
    sorted[sorted.len() / 2]
}

fn image_encoder(path: &Path, rgb: &[u8], width: u32, height: u32) {
    let mut file = fs::File::create(path).unwrap();
    JpegEncoder::new_with_quality(&mut file, 90)
        .encode(rgb, width, height, ExtendedColorType::Rgb8)
        .unwrap();
}

fn focused_encoder(path: &Path, rgb: &[u8], width: u32, height: u32) {
    let mut encoder = Encoder::new_file(path, 90).unwrap();
    encoder.set_sampling_factor(SamplingFactor::R_4_4_4);
    encoder
        .encode(rgb, width as u16, height as u16, ColorType::Rgb)
        .unwrap();
}

fn main() {
    let root = repo_root();
    let source = root.join("test_images/ColorCard_Combo.JPG");
    let output = root.join("_ui_stress/jpeg-profile");
    fs::create_dir_all(&output).unwrap();
    let image = ImageReader::open(&source)
        .unwrap()
        .decode()
        .unwrap()
        .to_rgb8();
    let (width, height) = image.dimensions();
    let rgb = image.as_raw();
    let old_path = output.join("image-q90.jpg");
    let fast_path = output.join("focused-q90-444.jpg");
    let old = timed(|| image_encoder(&old_path, rgb, width, height));
    let fast = timed(|| focused_encoder(&fast_path, rgb, width, height));
    let report = json!({
        "pixels": [width, height],
        "image_encoder": {"p50_ms": p50(&old), "times_ms": old, "bytes": fs::metadata(old_path).unwrap().len()},
        "focused_encoder": {"p50_ms": p50(&fast), "times_ms": fast, "bytes": fs::metadata(fast_path).unwrap().len()},
    });
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
}
