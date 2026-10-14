//! In-process step timings for apply / preview / FFC. Not a product command.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

use ccp_bridge::run_pipeline::{
    apply_saved_to_image_ex, load_saved, rgb_image_to_array3, write_preview_jpeg, write_still,
    ApplyOptions, PreviewMode,
};
use ccp_bridge::serve::prepare_libtorch_env;
use ccp_core::ffc::{
    apply_ffc_float, apply_ffc_gc_fused, compute_multiplier, FfcFitMethod, FfcFitParams,
};
use ccp_core::maps::{build_encoded_lut, APPLY_LUT_SIZE};
use ccp_core::sample::load_image;
use ccp_core::stages::{apply_cc_inplace, apply_gc_inplace, apply_wb_inplace};

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

fn timed_with_setup<T>(
    n_warm: usize,
    n: usize,
    mut setup: impl FnMut() -> T,
    mut f: impl FnMut(&mut T),
) -> Vec<f64> {
    for _ in 0..n_warm {
        let mut value = setup();
        f(&mut value);
    }
    let mut times = Vec::with_capacity(n);
    for _ in 0..n {
        let mut value = setup();
        let t0 = Instant::now();
        f(&mut value);
        times.push(t0.elapsed().as_secs_f64() * 1000.0);
    }
    times
}

fn stats(times: &[f64]) -> serde_json::Value {
    let mut s = times.to_vec();
    s.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let mean = s.iter().sum::<f64>() / s.len() as f64;
    serde_json::json!({
        "n": s.len(),
        "min_ms": round(*s.first().unwrap_or(&0.0)),
        "p50_ms": round(s[s.len() / 2]),
        "max_ms": round(*s.last().unwrap_or(&0.0)),
        "mean_ms": round(mean),
        "times_ms": s.iter().map(|v| round(*v)).collect::<Vec<_>>(),
    })
}

fn round(v: f64) -> f64 {
    (v * 10.0).round() / 10.0
}

fn jpeg_decode_ms(path: &Path, n_warm: usize, n: usize) -> serde_json::Value {
    stats(&timed(n_warm, n, || {
        let _ = image::open(path).expect("jpeg decode");
    }))
}

fn write_report(path: &Path, report: &serde_json::Value) {
    fs::write(path, serde_json::to_string_pretty(report).unwrap()).unwrap();
}

fn main() {
    prepare_libtorch_env();
    let root = repo_root();
    let combo = root.join("test_images/ColorCard_Combo.JPG");
    let blank = root.join("test_images/Blank.JPG");
    let model = root.join("_ui_stress/matrix/rpcc2-full-ffc-linear/model.json");
    let out_dir = root.join("_ui_stress/apply-profile");
    let dest = root.join("_ui_stress/apply-profile.json");
    let _ = fs::remove_dir_all(&out_dir);
    fs::create_dir_all(&out_dir).unwrap();

    let saved = load_saved(&model).expect("model");
    let (lut, settings) = saved.lut_pipeline().expect("lut");
    let ffc = FfcFitParams {
        method: FfcFitMethod::Linear,
        bins: 50,
        smooth_window: 5,
        degree: 3,
        ..FfcFitParams::default()
    };

    eprintln!("load source");
    let load_src = timed(1, 5, || {
        let _ = load_image(&combo).unwrap();
    });
    let src = load_image(&combo).unwrap();
    let to_f32 = timed(1, 5, || {
        let _ = rgb_image_to_array3(&src).unwrap();
    });
    let mut work = rgb_image_to_array3(&src).unwrap();
    let source_work = work.clone();

    eprintln!("load white + FFC fit (full-frame, no plane ONNX)");
    let load_white = timed(1, 5, || {
        let _ = load_image(&blank).unwrap();
    });
    let white_img = load_image(&blank).unwrap();
    let white_arr = rgb_image_to_array3(&white_img).unwrap();
    let rect = [0, 0, white_img.width as i32, white_img.height as i32];
    let ffc_fit = timed(1, 5, || {
        let _ = compute_multiplier(Some(&white_arr), Some(rect), &ffc).unwrap();
    });
    let gain = compute_multiplier(Some(&white_arr), Some(rect), &ffc).unwrap();
    drop(white_arr);
    drop(white_img);

    eprintln!("FFC/GC/WB/CC apply");
    let ffc_apply = timed_with_setup(
        1,
        5,
        || source_work.clone(),
        |image| {
            *image = apply_ffc_float(image, &gain);
        },
    );
    work = rgb_image_to_array3(&src).unwrap();

    let gc = lut.gc.as_ref().expect("gc");
    let gc_apply = timed_with_setup(
        1,
        5,
        || source_work.clone(),
        |image| {
            apply_gc_inplace(image, gc).unwrap();
        },
    );
    let ffc_gc_separate = timed_with_setup(
        1,
        5,
        || source_work.clone(),
        |image| {
            *image = apply_ffc_float(image, &gain);
            apply_gc_inplace(image, gc).unwrap();
        },
    );
    let ffc_gc_fused = timed_with_setup(
        1,
        5,
        || source_work.clone(),
        |image| {
            *image = apply_ffc_gc_fused(image, &gain, gc);
        },
    );
    let wb = lut.wb.as_ref().expect("wb");
    let wb_apply = timed_with_setup(
        1,
        5,
        || source_work.clone(),
        |image| {
            apply_wb_inplace(image, wb);
        },
    );
    let cc = lut.cc.as_ref().expect("cc");
    let lut_build = timed(1, 5, || {
        let _ = build_encoded_lut(&cc.0, &cc.1, APPLY_LUT_SIZE).unwrap();
    });
    let cc_apply = timed_with_setup(
        1,
        5,
        || source_work.clone(),
        |image| {
            apply_cc_inplace(image, &cc.0, &settings).unwrap();
        },
    );

    apply_gc_inplace(&mut work, gc).unwrap();
    apply_wb_inplace(&mut work, wb);
    apply_cc_inplace(&mut work, &cc.0, &settings).unwrap();

    eprintln!("JPEG writes");
    let preview_1 = {
        let path = out_dir.join("one.jpg");
        timed(1, 5, || write_preview_jpeg(&work, &path).unwrap())
    };
    let preview_6 = timed(1, 5, || {
        for name in ["original", "ffc", "gc", "wb", "cc", "corrected"] {
            write_preview_jpeg(&work, out_dir.join(format!("{name}.jpg"))).unwrap();
        }
    });
    let output_q90 = {
        let path = out_dir.join("full_corrected.jpg");
        timed(1, 5, || write_still(&work, &path).unwrap())
    };

    let mut report = serde_json::json!({
        "image": combo,
        "white": blank,
        "model": model,
        "pixels": [src.width, src.height],
        "notes": [
            "In-process Rust timings. 1 warmup + 5 repeats unless noted.",
            "FFC fit uses a full-frame rect (no plane ONNX) so the number is gain-fit only.",
            "Stage apply resets from the source f32 each repeat.",
            "preview x6 encodes the same corrected buffer six times.",
        ],
        "steps": {
            "load_source_jpeg": stats(&load_src),
            "rgb_to_f32_clone": stats(&to_f32),
            "load_white_jpeg": stats(&load_white),
            "ffc_fit_fullframe": stats(&ffc_fit),
            "ffc_apply_lab": stats(&ffc_apply),
            "gc_apply_lab": stats(&gc_apply),
            "ffc_gc_separate": stats(&ffc_gc_separate),
            "ffc_gc_fused": stats(&ffc_gc_fused),
            "wb_apply": stats(&wb_apply),
            "cc_lut_build_17": stats(&lut_build),
            "cc_apply_lut_and_sample": stats(&cc_apply),
            "write_preview_2560_q72": stats(&preview_1),
            "write_preview_x6": stats(&preview_6),
            "write_output_full_q90": stats(&output_q90),
            "decode_source_jpeg": jpeg_decode_ms(&combo, 1, 5),
            "decode_preview_2560_jpeg": jpeg_decode_ms(&out_dir.join("corrected.jpg"), 1, 5),
            "decode_output_6000_jpeg": jpeg_decode_ms(&out_dir.join("full_corrected.jpg"), 1, 5),
        },
        "preview_file_bytes": fs::metadata(out_dir.join("corrected.jpg")).map(|m| m.len()).unwrap_or(0),
        "output_file_bytes": fs::metadata(out_dir.join("full_corrected.jpg")).map(|m| m.len()).unwrap_or(0),
    });
    write_report(&dest, &report);
    eprintln!("steps written; running end-to-end apply");

    let apply_full = {
        let out = out_dir.join("e2e_full.jpg");
        let prev = out_dir.join("e2e_prev");
        let opts = ApplyOptions {
            do_ffc: true,
            do_gc: true,
            do_wb: true,
            do_cc: true,
            white: Some(blank.clone()),
            crop_rect: None,
            ffc: ffc.clone(),
            write_output: true,
            preview_mode: PreviewMode::All,
            ffc_cache: None,
        };
        timed(1, 3, || {
            let _ = fs::remove_dir_all(&prev);
            apply_saved_to_image_ex(&saved, &combo, &out, Some(&prev), &opts).unwrap();
        })
    };
    let apply_noprev = {
        let out = out_dir.join("e2e_noprev.jpg");
        let opts = ApplyOptions {
            do_ffc: true,
            do_gc: true,
            do_wb: true,
            do_cc: true,
            white: Some(blank.clone()),
            crop_rect: None,
            ffc: ffc.clone(),
            write_output: true,
            preview_mode: PreviewMode::None,
            ffc_cache: None,
        };
        timed(1, 3, || {
            apply_saved_to_image_ex(&saved, &combo, &out, None, &opts).unwrap();
        })
    };
    report["end_to_end_apply"] = serde_json::json!({
        "ffc_gc_wb_cc_plus_6_previews": stats(&apply_full),
        "ffc_gc_wb_cc_no_previews": stats(&apply_noprev),
    });
    write_report(&dest, &report);
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
    eprintln!("wrote {}", dest.display());
}
