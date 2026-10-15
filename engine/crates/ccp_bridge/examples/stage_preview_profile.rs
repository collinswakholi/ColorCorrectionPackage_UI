//! Fresh timings for stage + preview decisions. Does not change product code.
//! cargo run -p ccp_bridge --release --example stage_preview_profile --features tch,ort-plane

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Instant;

use ccp_bridge::run_pipeline::{
    apply_saved_to_image_ex, array3_to_rgb_image, load_saved, rgb_image_to_array3, run_from_json,
    write_preview_jpeg, write_preview_png, write_still, ApplyOptions, FfcGainCache, PreviewMode,
};
use ccp_bridge::serve::prepare_libtorch_env;
use ccp_core::ffc::{
    apply_ffc_float, apply_ffc_gc_fused, compute_multiplier, FfcFitMethod, FfcFitParams,
};
use ccp_core::sample::load_image;
use ccp_core::stages::apply_gc_inplace;
use ccp_detect::{detect_chart, warmup_detector, DetectSettings};
use image::codecs::jpeg::JpegEncoder;
use image::{imageops, ExtendedColorType};
use rayon::prelude::*;
use serde_json::{json, Value};

fn repo_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("repo root")
        .to_path_buf()
}

fn timed(n_warm: usize, n: usize, mut f: impl FnMut()) -> Vec<f64> {
    for _ in 0..n_warm {
        f();
    }
    let mut times = Vec::with_capacity(n);
    for _ in 0..n {
        let t0 = Instant::now();
        f();
        times.push(t0.elapsed().as_secs_f64() * 1000.0);
    }
    times
}

fn stats(times: &[f64]) -> Value {
    let mut s = times.to_vec();
    s.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let mean = if s.is_empty() {
        0.0
    } else {
        s.iter().sum::<f64>() / s.len() as f64
    };
    json!({
        "n": s.len(),
        "min_ms": round(*s.first().unwrap_or(&0.0)),
        "p50_ms": round(s.get(s.len() / 2).copied().unwrap_or(0.0)),
        "max_ms": round(*s.last().unwrap_or(&0.0)),
        "mean_ms": round(mean),
        "times_ms": s.iter().map(|v| round(*v)).collect::<Vec<_>>(),
    })
}

fn round(v: f64) -> f64 {
    (v * 10.0).round() / 10.0
}

fn ffc_params() -> FfcFitParams {
    FfcFitParams {
        method: FfcFitMethod::Linear,
        bins: 50,
        smooth_window: 5,
        degree: 3,
        ..FfcFitParams::default()
    }
}

fn apply_opts(
    white: &Path,
    write_output: bool,
    preview: PreviewMode,
    cache: Option<Arc<FfcGainCache>>,
) -> ApplyOptions {
    ApplyOptions {
        do_ffc: true,
        do_gc: true,
        do_wb: true,
        do_cc: true,
        white: Some(white.to_path_buf()),
        crop_rect: None,
        ffc: ffc_params(),
        write_output,
        preview_mode: preview,
        ffc_cache: cache,
    }
}

fn write_preview_sized(arr: &ndarray::Array3<f32>, path: &Path, max_w: u32, quality: u8) {
    let h = arr.shape()[0];
    let w = arr.shape()[1];
    let src = arr.as_slice().expect("contiguous f32 rgb");
    let (out_w, out_h) = if w as u32 <= max_w {
        (w, h)
    } else {
        let out_w = max_w as usize;
        (out_w, (h * out_w / w).max(1))
    };
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
                row[di] = (src[si].clamp(0.0, 1.0) * 255.0).round() as u8;
                row[di + 1] = (src[si + 1].clamp(0.0, 1.0) * 255.0).round() as u8;
                row[di + 2] = (src[si + 2].clamp(0.0, 1.0) * 255.0).round() as u8;
            }
        });
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let mut file = fs::File::create(path).unwrap();
    let mut enc = JpegEncoder::new_with_quality(&mut file, quality);
    enc.encode(&buf, out_w as u32, out_h as u32, ExtendedColorType::Rgb8)
        .unwrap();
}

fn decode_ms(path: &Path, n: usize) -> Value {
    stats(&timed(1, n, || {
        let _ = image::open(path).expect("decode");
    }))
}

fn main() {
    prepare_libtorch_env();
    let root = repo_root();
    let github = root.join("test_images");
    let combo = github.join("ColorCard_Combo.JPG");
    let big = github.join("ColorCard_Big.JPG");
    let sample = github.join("Sample_3572.JPG");
    let blank = github.join("Blank.JPG");
    let hs_combo = github.join("ColorCard_Combo_detect.json");
    let model = root.join("_ui_stress/matrix/rpcc2-full-ffc-linear/model.json");
    let out_dir = root.join("_ui_stress/stage-preview-profile");
    let dest = root.join("_ui_stress/stage-preview-profile.json");
    let _ = fs::remove_dir_all(&out_dir);
    fs::create_dir_all(&out_dir).unwrap();

    let saved = load_saved(&model).expect("model");
    let (lut, _) = saved.lut_pipeline().expect("lut");
    let gc = lut.gc.as_ref().expect("gc");

    eprintln!("load Combo + convert");
    let src = load_image(&combo).unwrap();
    let to_f32 = timed(1, 4, || {
        let _ = rgb_image_to_array3(&src).unwrap();
    });
    let work = rgb_image_to_array3(&src).unwrap();
    let to_rgb = timed(1, 4, || {
        let _ = array3_to_rgb_image(&work).unwrap();
    });

    eprintln!("white plane u8 + FFC compute");
    let white = load_image(&blank).unwrap();
    let white_arr = rgb_image_to_array3(&white).unwrap();
    let serial_u8 = timed(0, 3, || {
        let _: Vec<u8> = white
            .rgb
            .iter()
            .map(|v| (v.clamp(0.0, 1.0) * 255.0).round() as u8)
            .collect();
    });
    let parallel_u8 = timed(1, 3, || {
        let mut buf = vec![0u8; white.rgb.len()];
        buf.par_iter_mut()
            .zip(white.rgb.par_iter())
            .for_each(|(d, s)| *d = (s.clamp(0.0, 1.0) * 255.0).round() as u8);
    });
    let ffc_full = timed(0, 3, || {
        let _ = ccp_bridge::run_pipeline::compute_ffc_gain(&blank, None, &ffc_params()).unwrap();
    });
    let rect = [390, 235, 5130, 3379];
    let ffc_gain_only = timed(1, 3, || {
        let _ = compute_multiplier(Some(&white_arr), Some(rect), &ffc_params()).unwrap();
    });
    let gain = compute_multiplier(Some(&white_arr), Some(rect), &ffc_params()).unwrap();

    eprintln!("FFC / GC / fused");
    let ffc_apply = timed(1, 4, || {
        let _ = apply_ffc_float(&work, &gain);
    });
    let gc_apply = {
        let mut buf = work.clone();
        timed(1, 4, || {
            apply_gc_inplace(&mut buf, gc).unwrap();
            buf = work.clone();
        })
    };
    let fused = timed(1, 4, || {
        let _ = apply_ffc_gc_fused(&work, &gain, gc);
    });
    let mut corrected = apply_ffc_gc_fused(&work, &gain, gc);
    if let Some(wb) = &lut.wb {
        ccp_core::stages::apply_wb_inplace(&mut corrected, wb);
    }
    if let Some((cc, settings)) = &lut.cc {
        ccp_core::stages::apply_cc_inplace(&mut corrected, cc, settings).unwrap();
    }

    eprintln!("preview encode matrix");
    let mut preview_matrix = Vec::new();
    for &(max_w, q) in &[
        (2560u32, 72u8),
        (2560, 60),
        (1920, 72),
        (1920, 60),
        (1600, 72),
        (1280, 72),
        (1280, 55),
    ] {
        let path = out_dir.join(format!("prev_{max_w}_q{q}.jpg"));
        let times = timed(1, 3, || write_preview_sized(&corrected, &path, max_w, q));
        preview_matrix.push(json!({
            "max_w": max_w,
            "quality": q,
            "bytes": fs::metadata(&path).map(|m| m.len()).unwrap_or(0),
            "encode": stats(&times),
            "decode": decode_ms(&path, 4),
        }));
    }
    let product_preview = {
        let path = out_dir.join("product_2560_q72.jpg");
        timed(1, 4, || write_preview_jpeg(&corrected, &path).unwrap())
    };
    let product_preview_png = {
        let path = out_dir.join("product_2560_fast.png");
        timed(1, 4, || write_preview_png(&corrected, &path).unwrap())
    };
    let full_output = {
        let path = out_dir.join("full_q90.jpg");
        timed(1, 3, || write_still(&corrected, &path).unwrap())
    };
    let downsample_only = timed(1, 4, || {
        let rgb = {
            let h = corrected.shape()[0];
            let w = corrected.shape()[1];
            let src = corrected.as_slice().unwrap();
            let out_w = 2560usize;
            let out_h = (h * out_w / w).max(1);
            let mut buf = vec![0u8; out_w * out_h * 3];
            buf.par_chunks_exact_mut(out_w * 3)
                .enumerate()
                .for_each(|(y, row)| {
                    let sy = y * h / out_h;
                    let src_row = sy * w * 3;
                    for x in 0..out_w {
                        let sx = x * w / out_w;
                        let si = src_row + sx * 3;
                        row[x * 3] = (src[si].clamp(0.0, 1.0) * 255.0).round() as u8;
                        row[x * 3 + 1] = (src[si + 1].clamp(0.0, 1.0) * 255.0).round() as u8;
                        row[x * 3 + 2] = (src[si + 2].clamp(0.0, 1.0) * 255.0).round() as u8;
                    }
                });
            buf
        };
        std::hint::black_box(rgb);
    });

    let ui_source_decode = timed(1, 4, || {
        let img = image::open(&combo).unwrap().to_rgb8();
        let scaled = imageops::resize(&img, 2560, 1707, imageops::FilterType::Triangle);
        std::hint::black_box(scaled);
    });
    let ui_source_nn = timed(1, 4, || {
        let img = image::open(&combo).unwrap().to_rgb8();
        let scaled = imageops::resize(&img, 2560, 1707, imageops::FilterType::Nearest);
        std::hint::black_box(scaled);
    });

    eprintln!("end-to-end apply variants");
    let cache = Arc::new(FfcGainCache::new());
    let mut e2e = serde_json::Map::new();
    type ApplyCase<'a> = (
        &'a str,
        &'a Path,
        bool,
        PreviewMode,
        Option<Arc<FfcGainCache>>,
    );
    let cases: [ApplyCase<'_>; 8] = [
        ("combo_ui_now", &combo, false, PreviewMode::Corrected, None),
        (
            "combo_ui_ffc_cache",
            &combo,
            false,
            PreviewMode::Corrected,
            Some(cache.clone()),
        ),
        (
            "combo_output_plus_preview",
            &combo,
            true,
            PreviewMode::Corrected,
            Some(cache.clone()),
        ),
        (
            "combo_compute_only",
            &combo,
            false,
            PreviewMode::None,
            Some(cache.clone()),
        ),
        (
            "combo_preview_all",
            &combo,
            false,
            PreviewMode::All,
            Some(cache.clone()),
        ),
        (
            "combo_stage_ffc",
            &combo,
            false,
            PreviewMode::Stage("ffc".into()),
            Some(cache.clone()),
        ),
        (
            "big_ui_now",
            &big,
            false,
            PreviewMode::Corrected,
            Some(cache.clone()),
        ),
        (
            "sample_ui_now",
            &sample,
            false,
            PreviewMode::Corrected,
            Some(cache.clone()),
        ),
    ];
    for (name, image, write_out, mode, c) in cases {
        eprintln!("  {name}");
        let out = out_dir.join(format!("{name}.jpg"));
        let prev = out_dir.join(format!("{name}_prev"));
        let opts = apply_opts(blank.as_path(), write_out, mode, c);
        let n = if name.contains("combo_ui_now") || name.contains("compute") {
            3
        } else {
            2
        };
        let times = timed(1, n, || {
            let _ = fs::remove_dir_all(&prev);
            apply_saved_to_image_ex(&saved, image, &out, Some(&prev), &opts).unwrap();
        });
        e2e.insert(name.to_string(), stats(&times));
    }
    // Second apply on combo with warm cache (already warmed above).
    {
        let out = out_dir.join("combo_ui_ffc_cache2.jpg");
        let prev = out_dir.join("combo_ui_ffc_cache2_prev");
        let opts = apply_opts(
            blank.as_path(),
            false,
            PreviewMode::Corrected,
            Some(cache.clone()),
        );
        let times = timed(0, 3, || {
            let _ = fs::remove_dir_all(&prev);
            apply_saved_to_image_ex(&saved, &combo, &out, Some(&prev), &opts).unwrap();
        });
        e2e.insert("combo_ui_ffc_cache_repeat".into(), stats(&times));
    }

    eprintln!("detect");
    let settings = DetectSettings {
        device: "gpu".into(),
        ..DetectSettings::default()
    };
    let warm = {
        let t0 = Instant::now();
        let _ = warmup_detector(&settings);
        t0.elapsed().as_secs_f64() * 1000.0
    };
    let detect_combo = timed(0, 3, || {
        let r = detect_chart(&combo, &settings);
        if !r.ok {
            eprintln!("detect combo failed: {}", r.error);
        }
    });
    let detect_sample = timed(0, 2, || {
        let r = detect_chart(&sample, &settings);
        if !r.ok {
            eprintln!("detect sample failed: {}", r.error);
        }
    });

    eprintln!("fit");
    let mut fit = serde_json::Map::new();
    for (name, samples, write_out, preview) in [
        ("combo_fit_ui_now", 1i64, false, "corrected"),
        ("combo_fit_samples9", 9i64, false, "corrected"),
        ("combo_fit_plus_output", 1i64, true, "corrected"),
        ("combo_fit_noprev", 1i64, false, "none"),
    ] {
        eprintln!("  {name}");
        let dir = out_dir.join(name);
        fs::create_dir_all(&dir).unwrap();
        let req = json!({
            "image": combo,
            "handshake": hs_combo,
            "output": dir.join("corrected.jpg"),
            "report": dir.join("report.json"),
            "model_out": dir.join("model.json"),
            "preview_dir": dir.join("prev"),
            "write_output": write_out,
            "preview_mode": preview,
            "model": "rpcc2",
            "crop": 0.75,
            "use_all": true,
            "do_ffc": true,
            "do_gc": true,
            "do_wb": true,
            "do_cc": true,
            "do_sat": true,
            "fit_samples": samples,
            "include_combinations": true,
            "white": blank,
            "ffc": {"method": "linear", "bins": 50, "smooth_window": 5, "degree": 3},
        });
        let times = timed(1, 2, || {
            let _ = fs::remove_dir_all(dir.join("prev"));
            let _ = run_from_json(&req, &mut |_| {}).unwrap();
        });
        fit.insert(name.to_string(), stats(&times));
    }

    let report = json!({
        "when": chrono_like(),
        "image_px": [src.width, src.height],
        "notes": [
            "In-process Rust. 1 warmup + N repeats unless noted.",
            "UI Apply today: write_output=false, preview_mode=corrected, FFC cache in the worker.",
            "Interactive preview is PNG-fast (NoFilter, 2560). Export still writes full JPEG q90.",
            "Batch/Save set write_output=true (full 6000 q90 JPEG).",
            "Stage radio calls preview_stage and re-applies the pipeline for one preview.",
        ],
        "steps": {
            "rgb_to_f32": stats(&to_f32),
            "f32_to_rgb_image_fit_sample": stats(&to_rgb),
            "white_u8_serial": stats(&serial_u8),
            "white_u8_parallel": stats(&parallel_u8),
            "ffc_compute_plane_plus_gain": stats(&ffc_full),
            "ffc_gain_known_rect": stats(&ffc_gain_only),
            "ffc_apply": stats(&ffc_apply),
            "gc_apply": stats(&gc_apply),
            "ffc_gc_fused": stats(&fused),
            "write_preview_product_2560_q72": stats(&product_preview),
            "write_preview_product_2560_png_fast": stats(&product_preview_png),
            "write_output_full_q90": stats(&full_output),
            "downsample_2560_only": stats(&downsample_only),
            "ui_decode_source_triangle_2560": stats(&ui_source_decode),
            "ui_decode_source_nearest_2560": stats(&ui_source_nn),
            "decode_source_full": decode_ms(&combo, 4),
            "decode_product_preview": decode_ms(&out_dir.join("product_2560_q72.jpg"), 4),
            "decode_product_preview_png": decode_ms(&out_dir.join("product_2560_fast.png"), 4),
        },
        "preview_matrix": preview_matrix,
        "end_to_end_apply": e2e,
        "detect": {
            "warmup_ms": round(warm),
            "combo": stats(&detect_combo),
            "sample_3572": stats(&detect_sample),
        },
        "fit": fit,
        "product_preview_bytes": fs::metadata(out_dir.join("product_2560_q72.jpg")).map(|m| m.len()).unwrap_or(0),
        "product_preview_png_bytes": fs::metadata(out_dir.join("product_2560_fast.png")).map(|m| m.len()).unwrap_or(0),
        "full_output_bytes": fs::metadata(out_dir.join("full_q90.jpg")).map(|m| m.len()).unwrap_or(0),
    });
    fs::write(&dest, serde_json::to_string_pretty(&report).unwrap()).unwrap();
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
    eprintln!("wrote {}", dest.display());
}

fn chrono_like() -> String {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_secs();
    format!("unix {now}")
}
