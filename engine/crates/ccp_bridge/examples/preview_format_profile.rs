//! Encode-only timings on an already-sized 2560 preview buffer.
use std::fs;
use std::path::PathBuf;
use std::time::Instant;

use image::codecs::jpeg::JpegEncoder;
use image::codecs::png::{CompressionType, FilterType, PngEncoder};
use image::{ExtendedColorType, ImageEncoder};

fn main() {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .unwrap()
        .to_path_buf();
    let src = root.join("_ui_stress/stage-preview-profile/product_2560_q72.jpg");
    let dir = root.join("_ui_stress/stage-preview-profile/formats");
    fs::create_dir_all(&dir).unwrap();
    let img = image::open(&src).unwrap().to_rgb8();
    let (w, h) = img.dimensions();
    let raw = img.as_raw();
    eprintln!("buffer {w}x{h} {} bytes", raw.len());

    let mut rows = Vec::new();
    for (name, mut write) in [
        (
            "jpeg_q72",
            Box::new(|p: PathBuf, raw: &[u8], w: u32, h: u32| {
                let mut f = fs::File::create(p).unwrap();
                JpegEncoder::new_with_quality(&mut f, 72)
                    .encode(raw, w, h, ExtendedColorType::Rgb8)
                    .unwrap();
            }) as Box<dyn FnMut(PathBuf, &[u8], u32, u32)>,
        ),
        (
            "jpeg_q50",
            Box::new(|p: PathBuf, raw: &[u8], w: u32, h: u32| {
                let mut f = fs::File::create(p).unwrap();
                JpegEncoder::new_with_quality(&mut f, 50)
                    .encode(raw, w, h, ExtendedColorType::Rgb8)
                    .unwrap();
            }),
        ),
        (
            "png_fast",
            Box::new(|p: PathBuf, raw: &[u8], w: u32, h: u32| {
                let f = fs::File::create(p).unwrap();
                PngEncoder::new_with_quality(
                    std::io::BufWriter::new(f),
                    CompressionType::Fast,
                    FilterType::NoFilter,
                )
                .write_image(raw, w, h, ExtendedColorType::Rgb8)
                .unwrap();
            }),
        ),
        (
            "raw_rgb",
            Box::new(|p: PathBuf, raw: &[u8], _w: u32, _h: u32| {
                fs::write(p, raw).unwrap();
            }),
        ),
    ] {
        for _ in 0..1 {
            write(dir.join(format!("{name}_warm")), raw, w, h);
        }
        let mut times = Vec::new();
        for i in 0..4 {
            let t0 = Instant::now();
            write(dir.join(format!("{name}_{i}")), raw, w, h);
            times.push(t0.elapsed().as_secs_f64() * 1000.0);
        }
        times.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let bytes = fs::metadata(dir.join(format!("{name}_0")))
            .map(|m| m.len())
            .unwrap_or(0);
        let mean = times.iter().sum::<f64>() / times.len() as f64;
        rows.push(serde_json::json!({
            "name": name,
            "p50_ms": (times[times.len()/2] * 10.0).round() / 10.0,
            "mean_ms": (mean * 10.0).round() / 10.0,
            "bytes": bytes,
        }));
        eprintln!(
            "{name}: p50 {:.1} ms  {} bytes",
            times[times.len() / 2],
            bytes
        );
    }
    let dest = root.join("_ui_stress/preview-format-profile.json");
    fs::write(&dest, serde_json::to_string_pretty(&rows).unwrap()).unwrap();
}
