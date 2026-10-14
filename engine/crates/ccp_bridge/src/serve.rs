//! First-class NDJSON serve (stdin/stdout). Host drains stderr.

use std::collections::HashMap;
use std::io::{BufRead, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::SystemTime;

use ccp_core::lut::DEFAULT_LUT_SIZE;
use ccp_detect::paths::{default_libtorch_root, libtorch_version_matches_29, read_libtorch_json};
use ccp_detect::plane::last_plane_ep;
use ccp_detect::{
    detect_chart, probe_accelerators, set_runtime_from_report, warmup_detector, DetectSettings,
    DevicePolicy, DEFAULT_CONF, DEFAULT_CROP,
};
use serde_json::{json, Value};

use crate::run_pipeline::{
    apply_saved_to_image_ex, export_cube_from_saved, json_bool, json_f64, json_i64, json_str,
    last_apply_backend, load_saved, run_from_json_ex, ApplyOptions, FfcGainCache, PreviewMode,
    SavedPipeline,
};

#[cfg(feature = "video")]
use crate::run_pipeline::{
    apply_saved_to_work, array3_to_u8_rgb, compute_ffc_gain, u8_rgb_to_array3,
};

#[derive(Debug)]
pub struct ServeState {
    pub policy: String,
    pub device: String,
    pub warmed: bool,
    pub last: Option<SavedPipeline>,
    pub detect_cache: HashMap<String, PathBuf>,
    pub opt2_block: Option<String>,
    pub ffc_cache: Arc<FfcGainCache>,
}

impl Default for ServeState {
    fn default() -> Self {
        Self {
            policy: "auto".into(),
            device: "cpu".into(),
            warmed: false,
            last: None,
            detect_cache: HashMap::new(),
            opt2_block: None,
            ffc_cache: Arc::new(FfcGainCache::new()),
        }
    }
}

/// Set PATH / LIBTORCH / LIBTORCH_PATH from the single `default_libtorch_root()`.
/// Call before any tch load. Never point at two LibTorch trees.
pub fn prepare_libtorch_env() {
    use std::sync::Once;
    static ONCE: Once = Once::new();
    ONCE.call_once(|| {
        let root = default_libtorch_root();
        std::env::set_var("LIBTORCH", &root);
        std::env::set_var("LIBTORCH_PATH", &root);
        std::env::set_var("KMP_DUPLICATE_LIB_OK", "TRUE");

        let mut prepend = vec![root.clone()];
        #[cfg(windows)]
        {
            prepend.push(root.join("lib"));
            prepend.push(root.join("libtorch").join("lib"));
        }
        #[cfg(not(windows))]
        {
            prepend.push(root.join("lib"));
            prepend.push(root.join("libtorch").join("lib"));
        }

        let extra: Vec<String> = prepend
            .into_iter()
            .map(|p| p.to_string_lossy().into_owned())
            .collect();
        let current = std::env::var("PATH").unwrap_or_default();
        let mut parts: Vec<String> = extra;
        for piece in std::env::split_paths(&current) {
            let s = piece.to_string_lossy().into_owned();
            if !parts.iter().any(|p| p == &s) {
                parts.push(s);
            }
        }
        let joined = std::env::join_paths(parts.iter().map(std::ffi::OsString::from))
            .unwrap_or_else(|_| std::ffi::OsString::from(current));
        std::env::set_var("PATH", joined);
        preload_torch_cuda(&root);
    });
}

/// Windows: `torch_cuda.dll` is delay-loaded. If it is not mapped before the first
/// `Device::Cuda` tensor, LibTorch reports no CUDA kernels (`aten::empty_strided`).
fn preload_torch_cuda(root: &std::path::Path) {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        let lib = root.join("lib");
        for name in ["c10_cuda.dll", "torch_cuda.dll"] {
            let path = lib.join(name);
            if !path.is_file() {
                continue;
            }
            let wide: Vec<u16> = path
                .as_os_str()
                .encode_wide()
                .chain(std::iter::once(0))
                .collect();
            let handle = unsafe { winload::LoadLibraryW(wide.as_ptr()) };
            if handle == 0 {
                eprintln!("ccp-engine: LoadLibraryW failed for {}", path.display());
            }
        }
    }
    let _ = root;
}

#[cfg(windows)]
mod winload {
    #[link(name = "kernel32")]
    extern "system" {
        pub fn LoadLibraryW(name: *const u16) -> isize;
    }
}

pub fn opt2_version_error() -> Option<String> {
    let root = default_libtorch_root();
    let json = read_libtorch_json(&root)?;
    if json.version.is_empty() {
        return None;
    }
    if libtorch_version_matches_29(&json.version) {
        return None;
    }
    Some(format!(
        "refuse Opt2: libtorch.json.version {} is not 2.9.x (single libtorch_root {})",
        json.version,
        root.display()
    ))
}

fn is_oom(err: &str) -> bool {
    let e = err.to_ascii_lowercase();
    e.contains("out of memory") || e.contains("oom") || e.contains("oom_on_warmup")
}

fn with_accel(mut value: Value) -> Value {
    if let Some(obj) = value.as_object_mut() {
        obj.insert("apply_backend".into(), json!(last_apply_backend()));
        if let Some(plane) = last_plane_ep() {
            obj.insert("plane_backend".into(), json!(plane));
        }
    }
    value
}

fn emit_progress(msg: &str) {
    let _ = writeln!(
        std::io::stdout(),
        "{}",
        json!({"type":"progress","message": msg})
    );
    let _ = std::io::stdout().flush();
}

fn emit_json(v: &Value) {
    let _ = writeln!(std::io::stdout(), "{v}");
    let _ = std::io::stdout().flush();
}

fn policy_from_req(req: &Value, fallback: &str) -> String {
    json_str(req, &["policy", "device"])
        .unwrap_or(fallback)
        .to_string()
}

fn detect_settings(state: &ServeState, req: &Value) -> DetectSettings {
    let mut settings = DetectSettings::default();
    let policy = DevicePolicy::parse(&policy_from_req(req, &state.policy));
    settings.device = match policy {
        DevicePolicy::Cpu => "cpu".into(),
        DevicePolicy::Cuda => "cuda".into(),
        DevicePolicy::Mlx => "mps".into(),
        DevicePolicy::DirectMl => "directml".into(),
        DevicePolicy::OpenVino => "openvino".into(),
        _ => {
            if state.device == "cpu" && state.warmed {
                "cpu".into()
            } else {
                "gpu".into()
            }
        }
    };
    settings.prefer = json_str(req, &["prefer"]).unwrap_or("").to_string();
    settings.conf = json_f64(req, &["conf"], f64::from(DEFAULT_CONF)) as f32;
    settings.crop = json_f64(req, &["crop"], DEFAULT_CROP);
    settings.allow_recall = json_bool(req, &["allow_recall"], true);
    settings
}

fn detect_cache_key(path: &Path, crop: f64, prefer: &str) -> String {
    let mtime = std::fs::metadata(path)
        .and_then(|m| m.modified())
        .unwrap_or(SystemTime::UNIX_EPOCH);
    let epoch = mtime
        .duration_since(SystemTime::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    format!("{}|{epoch}|{crop:.6}|{prefer}", path.display())
}

fn ready_title(warmed: bool, policy: &str) -> String {
    let report = probe_accelerators(policy);
    let warm = if warmed { "Warmed" } else { "Ready" };
    format!("{warm} · {}", report.recommended)
}

fn emit_ready(state: &ServeState) {
    let mut ready = json!({
        "ok": true,
        "event": "ready",
        "device": state.device,
        "warmed": state.warmed,
        "title": ready_title(state.warmed, &state.policy),
    });
    if let Some(err) = &state.opt2_block {
        ready["opt2_ok"] = json!(false);
        ready["error"] = json!(err);
    }
    emit_json(&ready);
}

fn cmd_devices(state: &ServeState, req: &Value) -> Value {
    let policy = policy_from_req(req, &state.policy);
    let report = probe_accelerators(&policy);
    set_runtime_from_report(&report);
    let mut v = serde_json::to_value(&report).unwrap_or_else(|_| json!({}));
    if let Some(obj) = v.as_object_mut() {
        obj.insert("ok".into(), json!(true));
    }
    v
}

fn cmd_warmup(state: &mut ServeState, req: &Value) -> Value {
    if let Some(err) = &state.opt2_block {
        return json!({"ok":false,"warmed":false,"error": err});
    }
    emit_progress("Warming Opt2 1280");
    state.policy = policy_from_req(req, &state.policy);
    set_runtime_from_report(&probe_accelerators(&state.policy));
    let mut settings = detect_settings(state, req);
    let result = warmup_detector(&settings);
    if !result.ok && is_oom(&result.error) {
        emit_progress("OOM on warmup; falling back to CPU");
        settings.device = "cpu".into();
        let cpu = warmup_detector(&settings);
        if cpu.ok {
            state.device = "cpu".into();
            state.warmed = true;
            return json!({
                "ok": true,
                "warmed": true,
                "device": "cpu",
                "failed": "oom_on_warmup",
                "title": ready_title(true, "cpu"),
            });
        }
        return json!({
            "ok": false,
            "warmed": false,
            "device": "cpu",
            "error": format!("failed oom_on_warmup ({})", result.error),
        });
    }
    if result.ok {
        state.device = if result.device.is_empty() {
            settings.device
        } else {
            result.device.clone()
        };
        state.warmed = true;
        json!({
            "ok": true,
            "warmed": true,
            "device": state.device,
            "title": ready_title(true, &state.policy),
        })
    } else {
        json!({
            "ok": false,
            "warmed": false,
            "device": state.device,
            "error": result.error,
        })
    }
}

fn cmd_detect(state: &mut ServeState, req: &Value) -> Value {
    if let Some(err) = &state.opt2_block {
        return json!({"ok":false,"error": err});
    }
    let image = match json_str(req, &["image", "input", "image_path"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"detect requires image"}),
    };
    if !image.is_file() {
        return json!({"ok":false,"error": format!("image not found: {}", image.display())});
    }
    let settings = detect_settings(state, req);
    let key = detect_cache_key(&image, settings.crop, &settings.prefer);
    if let Some(cached) = state.detect_cache.get(&key) {
        if cached.is_file() {
            if let Ok(text) = std::fs::read_to_string(cached) {
                if !text.trim().is_empty() {
                    if let Ok(hs) = serde_json::from_str::<Value>(&text) {
                        return json!({
                            "ok": true,
                            "handshake": cached,
                            "n_charts": hs.get("n_charts").cloned().unwrap_or(json!(1)),
                            "chart_type": hs.get("chart_type").cloned().unwrap_or(json!("")),
                            "cached": true,
                        });
                    }
                }
            }
        }
    }

    emit_progress("Detecting chart");
    let result = detect_chart(&image, &settings);
    if !result.ok {
        return json!({
            "ok": false,
            "error": result.error,
            "n_charts": result.n_charts,
            "chart_type": result.chart_type,
        });
    }
    if result.json.trim().is_empty() {
        return json!({
            "ok": false,
            "error": "detect produced an empty handshake",
            "n_charts": result.n_charts,
            "chart_type": result.chart_type,
        });
    }
    let handshake_out = json_str(req, &["handshake_out", "json_out"]).map(PathBuf::from);
    let out = handshake_out.unwrap_or_else(|| {
        let mut p = std::env::temp_dir();
        let stem = image
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("detect");
        p.push(format!("ccp-detect-{stem}-{}.json", std::process::id()));
        p
    });
    if let Some(parent) = out.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Err(e) = std::fs::write(&out, &result.json) {
        return json!({"ok":false,"error": e.to_string()});
    }
    state.detect_cache.insert(key, out.clone());
    if !result.device.is_empty() {
        state.device = result.device.clone();
    }
    json!({
        "ok": true,
        "handshake": out,
        "n_charts": result.n_charts,
        "chart_type": result.chart_type,
        "device": result.device,
        "recall_source": result.recall_source,
        "cached": false,
    })
}

fn sync_runtime(state: &ServeState, req: &Value) {
    let policy = policy_from_req(req, &state.policy);
    set_runtime_from_report(&probe_accelerators(&policy));
}

fn cmd_run(state: &mut ServeState, req: &Value) -> Value {
    state.policy = policy_from_req(req, &state.policy);
    sync_runtime(state, req);
    match run_from_json_ex(
        req,
        &mut |m| emit_progress(m),
        Some(state.ffc_cache.clone()),
    ) {
        Ok(out) => {
            state.last = Some(out.saved.clone());
            with_accel(json!({
                "ok": true,
                "output": out.output,
                "report": out.report,
                "model_out": out.model_out,
                "lut_out": out.lut_out,
                "preview_dir": out.preview_dir,
                "chart_type": out.chart_type,
                "has_delta_e": out.has_delta_e,
                "report_json": out.report_json,
            }))
        }
        Err(e) => json!({"ok": false, "error": e.to_string()}),
    }
}

fn unique_artifact_bases(images: &[String]) -> Vec<String> {
    let stems: Vec<String> = images
        .iter()
        .map(|image| {
            Path::new(image)
                .file_stem()
                .and_then(|stem| stem.to_str())
                .filter(|stem| !stem.is_empty())
                .unwrap_or("out")
                .to_string()
        })
        .collect();
    let mut totals = HashMap::<String, usize>::new();
    for stem in &stems {
        *totals.entry(stem.to_ascii_lowercase()).or_default() += 1;
    }
    let mut occurrences = HashMap::<String, usize>::new();
    stems
        .into_iter()
        .map(|stem| {
            let key = stem.to_ascii_lowercase();
            if totals.get(&key).copied().unwrap_or(0) <= 1 {
                return stem;
            }
            let occurrence = occurrences.entry(key).or_default();
            *occurrence += 1;
            format!("{stem}_{}", *occurrence)
        })
        .collect()
}

fn cmd_apply(state: &ServeState, req: &Value) -> Value {
    sync_runtime(state, req);
    let model_path = match json_str(req, &["model", "model_path", "model_in"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"apply requires model"}),
    };
    let saved = match load_saved(&model_path) {
        Ok(s) => s,
        Err(e) => return json!({"ok":false,"error": e.to_string()}),
    };
    let images = req
        .get("images")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    if images.is_empty() {
        return json!({"ok":false,"error":"apply requires images"});
    }
    let output_dir = match json_str(req, &["output_dir", "output"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"apply requires output_dir"}),
    };
    let _ = std::fs::create_dir_all(&output_dir);
    emit_progress(&format!("Applying model to {} stills", images.len()));

    let preview_root = json_str(req, &["preview_dir"]).map(PathBuf::from);
    let valid_images: Vec<String> = images
        .iter()
        .filter_map(|v| v.as_str().map(|s| s.to_string()))
        .collect();
    let artifact_bases = unique_artifact_bases(&valid_images);
    let output_ext = match json_str(req, &["output_format"])
        .unwrap_or("jpg")
        .to_ascii_lowercase()
        .as_str()
    {
        "png" => "png",
        _ => "jpg",
    };
    let jobs: Vec<(String, PathBuf, Option<PathBuf>)> = valid_images
        .into_iter()
        .zip(artifact_bases)
        .map(|(img, artifact_base)| {
            let dest = output_dir.join(format!("{artifact_base}_corrected.{output_ext}"));
            let preview = preview_root.as_ref().map(|root| {
                let dir = if images.len() > 1 {
                    root.join(&artifact_base)
                } else {
                    root.clone()
                };
                let _ = std::fs::create_dir_all(&dir);
                dir
            });
            (img, dest, preview)
        })
        .collect();

    let mut apply = ApplyOptions::from_req(req, &saved);
    apply.ffc_cache = Some(state.ffc_cache.clone());
    if apply.do_ffc {
        if let Some(white) = &apply.white {
            let _ = state
                .ffc_cache
                .get_or_compute(white, apply.crop_rect.as_deref(), &apply.ffc);
        }
    }
    let apply_one = |img: &str, out: &Path, preview: Option<&Path>| -> Value {
        emit_progress(&format!(
            "Apply · {}",
            Path::new(img)
                .file_name()
                .and_then(|s| s.to_str())
                .unwrap_or(img)
        ));
        match apply_saved_to_image_ex(&saved, Path::new(img), out, preview, &apply) {
            Ok(report) => {
                let mut row = json!({
                    "ok": true,
                    "image": img,
                    "output": out,
                    "preview_dir": preview,
                    "metrics_hidden": true,
                });
                if let Some(skip) = report.ffc_skipped {
                    row["ffc_skipped"] = json!(skip);
                }
                with_accel(row)
            }
            Err(e) => json!({
                "ok": false,
                "image": img,
                "error": e.to_string(),
                "metrics_hidden": true,
            }),
        }
    };
    // Full-frame kernels already saturate the shared Rayon pool. Running whole
    // images concurrently caused nested contention and multiplied peak memory;
    // measured sequential dispatch is faster for both previews and JPEG export.
    let results: Vec<Value> = jobs
        .iter()
        .map(|(img, out, preview)| apply_one(img, out, preview.as_deref()))
        .collect();
    let failed = results.iter().filter(|r| r["ok"] == false).count();
    with_accel(json!({
        "ok": failed < results.len(),
        "results": results,
        "metrics_hidden": true,
        "error": if failed > 0 {
            Some(format!("{failed} of {} images failed", results.len()))
        } else {
            None
        },
    }))
}

fn cmd_preview_stage(state: &ServeState, req: &Value) -> Value {
    sync_runtime(state, req);
    let model_path = match json_str(req, &["model", "model_path", "model_in"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"preview_stage requires model"}),
    };
    let saved = match load_saved(&model_path) {
        Ok(s) => s,
        Err(e) => return json!({"ok":false,"error": e.to_string()}),
    };
    let image = match json_str(req, &["image", "input", "image_path"]) {
        Some(p) => PathBuf::from(p),
        None => {
            let first = req
                .get("images")
                .and_then(Value::as_array)
                .and_then(|a| a.first())
                .and_then(Value::as_str);
            match first {
                Some(p) => PathBuf::from(p),
                None => return json!({"ok":false,"error":"preview_stage requires image"}),
            }
        }
    };
    let preview_dir = match json_str(req, &["preview_dir"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"preview_stage requires preview_dir"}),
    };
    let stage = json_str(req, &["stage", "preview_mode"])
        .unwrap_or("corrected")
        .to_ascii_lowercase();
    let dummy = preview_dir.join("_unused.jpg");
    let mut apply = ApplyOptions::from_req(req, &saved);
    apply.write_output = false;
    apply.preview_mode = PreviewMode::Stage(stage.clone());
    apply.ffc_cache = Some(state.ffc_cache.clone());
    match stage.as_str() {
        "ffc" => {
            apply.do_gc = false;
            apply.do_wb = false;
            apply.do_cc = false;
        }
        "gc" => {
            apply.do_wb = false;
            apply.do_cc = false;
        }
        "wb" => {
            apply.do_cc = false;
        }
        "cc" | "corrected" => {}
        other => return json!({"ok":false,"error": format!("unknown stage: {other}")}),
    }
    match apply_saved_to_image_ex(&saved, &image, &dummy, Some(&preview_dir), &apply) {
        Ok(report) => {
            let mut row = json!({
                "ok": true,
                "stage": stage,
                "preview_dir": preview_dir,
            });
            if let Some(skip) = report.ffc_skipped {
                row["ffc_skipped"] = json!(skip);
            }
            with_accel(row)
        }
        Err(e) => json!({"ok": false, "error": e.to_string()}),
    }
}

fn cmd_export_cube(state: &ServeState, req: &Value) -> Value {
    // An explicit model must win over the last fit in a long-lived API session.
    if let Some(model) = json_str(req, &["model", "model_path"]) {
        return match load_saved(model) {
            Ok(saved) => write_cube_reply(&saved, req),
            Err(e) => json!({"ok":false,"error": e.to_string()}),
        };
    }
    let Some(saved) = &state.last else {
        return json!({"ok":false,"error":"export_cube needs a prior run or model"});
    };
    write_cube_reply(saved, req)
}

fn write_cube_reply(saved: &SavedPipeline, req: &Value) -> Value {
    let path = match json_str(req, &["path", "output", "lut_out"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"export_cube requires path"}),
    };
    let size = json_i64(req, &["size"], DEFAULT_LUT_SIZE as i64).max(2) as usize;
    match export_cube_from_saved(saved, &path, size) {
        Ok(()) => {
            json!({"ok":true,"path": path, "size": size, "note":"LUT does not include flat-field."})
        }
        Err(e) => json!({"ok":false,"error": e.to_string()}),
    }
}

#[cfg(feature = "video")]
fn cmd_video(state: &mut ServeState, req: &Value) -> Value {
    let input = match json_str(req, &["input", "image", "video"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"video requires input"}),
    };
    let output = match json_str(req, &["output"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"video requires output"}),
    };
    let cube_out = json_str(req, &["cube_out"])
        .map(PathBuf::from)
        .unwrap_or_else(|| output.with_extension("cube"));
    let report_path = output.with_extension("shot.json");
    let work = output
        .parent()
        .unwrap_or(Path::new("."))
        .join(format!("ccp-video-{}", std::process::id()));
    let _ = std::fs::create_dir_all(&work);

    let keyframe_sec = req.get("keyframe_sec").and_then(Value::as_f64);
    let sample_n = json_i64(req, &["sample_n"], 8).max(1) as usize;

    let frame = work.join("keyframe.jpg");
    let mut used_t = keyframe_sec.unwrap_or(0.0);
    if let Some(t) = keyframe_sec {
        emit_progress("Extracting keyframe");
        if let Err(e) = ccp_video::extract_frame(&input, t, &frame) {
            return json!({"ok":false,"error": e.to_string()});
        }
    } else {
        emit_progress("Sampling frames for first Opt2 hit");
        let thumbs = match ccp_video::timeline_thumbs(&input, 1.0, work.join("thumbs")) {
            Ok(t) => t,
            Err(e) => return json!({"ok":false,"error": e.to_string()}),
        };
        let take: Vec<_> = thumbs.into_iter().take(sample_n).collect();
        let mut hit = None;
        for (i, thumb) in take.iter().enumerate() {
            let mut detect_req = req.clone();
            if let Some(obj) = detect_req.as_object_mut() {
                obj.insert("image".into(), json!(thumb));
                obj.insert(
                    "handshake_out".into(),
                    json!(work.join(format!("hs_{i}.json"))),
                );
            }
            let det = cmd_detect(state, &detect_req);
            if det["ok"] == true && det["n_charts"].as_i64().unwrap_or(0) > 0 {
                hit = Some((thumb.clone(), det, i as f64));
                break;
            }
        }
        let Some((thumb, det, t)) = hit else {
            return json!({"ok":false,"error":"no Opt2 chart on sampled frames"});
        };
        used_t = t;
        let _ = std::fs::copy(&thumb, &frame);
        if let Some(hs) = det.get("handshake").and_then(Value::as_str) {
            let mut run_req = req.clone();
            if let Some(obj) = run_req.as_object_mut() {
                obj.insert("image".into(), json!(frame));
                obj.insert("handshake".into(), json!(hs));
                obj.insert("output".into(), json!(work.join("keyframe_corrected.png")));
                obj.insert("report".into(), json!(work.join("keyframe_report.json")));
                obj.insert("model_out".into(), json!(work.join("model.json")));
                obj.insert("lut_out".into(), json!(cube_out));
                obj.insert("do_ffc".into(), json!(false));
            }
            return finish_video(
                state,
                &run_req,
                req,
                &input,
                &output,
                &cube_out,
                &report_path,
                used_t,
            );
        }
    }

    if keyframe_sec.is_some() {
        let hs_out = work.join("handshake.json");
        let mut detect_req = req.clone();
        if let Some(obj) = detect_req.as_object_mut() {
            obj.insert("image".into(), json!(frame));
            obj.insert("handshake_out".into(), json!(hs_out));
        }
        let det = cmd_detect(state, &detect_req);
        if det["ok"] != true {
            return json!({"ok":false,"error": det.get("error").cloned().unwrap_or(json!("detect failed on keyframe"))});
        }
        let mut run_req = req.clone();
        if let Some(obj) = run_req.as_object_mut() {
            obj.insert("image".into(), json!(frame));
            obj.insert(
                "handshake".into(),
                det.get("handshake").cloned().unwrap_or(json!(hs_out)),
            );
            obj.insert("output".into(), json!(work.join("keyframe_corrected.png")));
            obj.insert("report".into(), json!(work.join("keyframe_report.json")));
            obj.insert("model_out".into(), json!(work.join("model.json")));
            obj.insert("lut_out".into(), json!(cube_out));
            obj.insert("do_ffc".into(), json!(false));
        }
        return finish_video(
            state,
            &run_req,
            req,
            &input,
            &output,
            &cube_out,
            &report_path,
            used_t,
        );
    }
    json!({"ok":false,"error":"video keyframe path failed"})
}

#[cfg(feature = "video")]
#[allow(clippy::too_many_arguments)] // Video completion consumes the explicit request/artifact set.
fn finish_video(
    state: &mut ServeState,
    run_req: &Value,
    grade_req: &Value,
    input: &Path,
    output: &Path,
    cube_out: &Path,
    report_path: &Path,
    keyframe: f64,
) -> Value {
    emit_progress("Fitting keyframe");
    let run = cmd_run(state, run_req);
    if run["ok"] != true {
        return run;
    }
    if let Err(e) = grade_clip(state.last.as_ref(), input, output, cube_out, grade_req) {
        return json!({"ok":false,"error": e});
    }
    let has_de = run["has_delta_e"].as_bool().unwrap_or(false);
    let saved = state.last.as_ref();
    let mut stages = Vec::new();
    if json_bool(grade_req, &["do_ffc"], false) {
        stages.push("ffc");
    }
    if json_bool(
        grade_req,
        &["do_gc"],
        saved.map(|s| s.do_gc).unwrap_or(true),
    ) {
        stages.push("gc");
    }
    if json_bool(
        grade_req,
        &["do_wb"],
        saved.map(|s| s.do_wb).unwrap_or(true),
    ) {
        stages.push("wb");
    }
    if json_bool(
        grade_req,
        &["do_cc"],
        saved.map(|s| s.do_cc).unwrap_or(true),
    ) {
        stages.push("cc");
    }
    let stage_names = if stages.is_empty() {
        "identity".into()
    } else {
        stages.join("+")
    };
    let transform = saved
        .map(|s| format!("{stage_names} ({})", s.kind))
        .unwrap_or(stage_names);
    let shot = ccp_video::ShotReport::v1(transform, keyframe, has_de);
    if let Err(e) = shot.write_json(report_path) {
        return json!({"ok":false,"error": format!("video report write failed: {e}")});
    }
    let model_out = run.get("model_out").cloned().unwrap_or(Value::Null);
    json!({
        "ok": true,
        "output": output,
        "cube": cube_out,
        "report": report_path,
        "model_out": model_out,
        "keyframe": keyframe,
        "chart_delta_e_exists": has_de,
        "transform": shot.transform,
        "note": shot.note,
    })
}

#[cfg(not(feature = "video"))]
fn cmd_video(_state: &mut ServeState, _req: &Value) -> Value {
    json!({"ok":false,"error":"ccp-engine was built without the video feature"})
}

#[cfg(feature = "video")]
fn cmd_video_grade(req: &Value) -> Value {
    let input = match json_str(req, &["input", "video"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"video_grade requires input"}),
    };
    let output = match json_str(req, &["output"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"video_grade requires output"}),
    };
    let model = match json_str(req, &["model", "model_path"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"video_grade requires a fitted model"}),
    };
    let saved = match load_saved(&model) {
        Ok(s) => s,
        Err(e) => return json!({"ok":false,"error": e.to_string()}),
    };
    let cube_out = json_str(req, &["cube_out"])
        .map(PathBuf::from)
        .unwrap_or_else(|| output.with_extension("cube"));
    if let Err(e) = grade_clip(Some(&saved), &input, &output, &cube_out, req) {
        return json!({"ok":false,"error": e});
    }
    json!({
        "ok": true,
        "output": output,
        "cube": cube_out,
        "note": ccp_video::LUT3D_NOTE,
        "ffc": json_bool(req, &["do_ffc"], false),
    })
}

#[cfg(feature = "video")]
fn video_pipeline(saved: &SavedPipeline, apply: &ApplyOptions) -> Result<SavedPipeline, String> {
    let mut selected = saved.clone();
    // FFC is handled per-frame, never inside the exported cube.
    selected.do_ffc = false;
    selected.do_gc = apply.do_gc;
    selected.do_wb = apply.do_wb;
    selected.do_cc = apply.do_cc;
    let (_, settings) = selected.lut_pipeline().map_err(|e| e.to_string())?;
    if settings.input_space.id != "srgb" || settings.input_space.white.id != "d65" {
        return Err("video requires a model calibrated for encoded sRGB/D65 input".into());
    }
    if selected.do_cc
        && (settings.target_space.id != "srgb" || settings.target_space.white.id != "d65")
    {
        return Err("video requires sRGB/D65 output; wide-gamut or linear output would be mislabeled Rec.709".into());
    }
    Ok(selected)
}

#[cfg(feature = "video")]
fn grade_clip(
    saved: Option<&SavedPipeline>,
    input: &Path,
    output: &Path,
    cube_out: &Path,
    req: &Value,
) -> std::result::Result<(), String> {
    let saved = saved.ok_or_else(|| "grade needs a fitted model".to_string())?;
    let apply = ApplyOptions::from_req(req, saved);
    let selected = video_pipeline(saved, &apply)?;
    if apply.do_ffc {
        if let Some(white) = &apply.white {
            emit_progress("Computing FFC gain from white");
            let gain = compute_ffc_gain(white, apply.crop_rect.as_deref(), &apply.ffc)
                .map_err(|e| e.to_string())?;
            emit_progress("Grading clip (FFC per frame, then GC/WB/CC)");
            ccp_video::map_rgb_frames(input, output, |w, h, rgb| {
                let mut work = u8_rgb_to_array3(w, h, rgb)
                    .map_err(|e| ccp_video::Error::InvalidArgument(e.to_string()))?;
                apply_saved_to_work(&mut work, saved, &apply, Some(&gain))
                    .map_err(|e| ccp_video::Error::InvalidArgument(e.to_string()))?;
                array3_to_u8_rgb(&work, rgb)
                    .map_err(|e| ccp_video::Error::InvalidArgument(e.to_string()))?;
                Ok(())
            })
            .map_err(|e| e.to_string())?;
            return Ok(());
        }
        emit_progress("FFC on but no white — cube-only grade");
    }
    emit_progress("Exporting LUT for video");
    export_cube_from_saved(&selected, cube_out, DEFAULT_LUT_SIZE).map_err(|e| e.to_string())?;
    emit_progress("Applying lut3d (no FFC in the cube)");
    ccp_video::apply_lut3d(input, cube_out, output).map_err(|e| e.to_string())?;
    Ok(())
}

#[cfg(not(feature = "video"))]
fn cmd_video_grade(_req: &Value) -> Value {
    json!({"ok":false,"error":"ccp-engine was built without the video feature"})
}

pub fn dispatch(state: &mut ServeState, req: &Value) -> (Value, bool) {
    let cmd = json_str(req, &["cmd", "command"])
        .unwrap_or("")
        .to_ascii_lowercase();
    match cmd.as_str() {
        "devices" => (cmd_devices(state, req), false),
        "warmup" => (cmd_warmup(state, req), false),
        "detect" => (cmd_detect(state, req), false),
        "run" => (cmd_run(state, req), false),
        "apply" => (cmd_apply(state, req), false),
        "preview_stage" => (cmd_preview_stage(state, req), false),
        "export_cube" => (cmd_export_cube(state, req), false),
        "video" => (cmd_video(state, req), false),
        "video_grade" => (cmd_video_grade(req), false),
        "shutdown" | "quit" => {
            crate::run_pipeline::flush_preview_encodes();
            (json!({"ok":true,"event":"shutdown"}), true)
        }
        "" => (json!({"ok":false,"error":"missing cmd"}), false),
        other => (
            json!({"ok":false,"error": format!("unknown cmd: {other}")}),
            false,
        ),
    }
}

/// Long-lived stdin/stdout loop. First line is the ready event.
pub fn run() -> Result<(), String> {
    prepare_libtorch_env();
    let mut state = ServeState {
        opt2_block: opt2_version_error(),
        ..ServeState::default()
    };
    let report = probe_accelerators(&state.policy);
    set_runtime_from_report(&report);
    state.device = match report.assignment.detect.as_str() {
        "cuda" => "cuda".into(),
        "mps" => "mps".into(),
        _ => "cpu".into(),
    };
    let threads = report.assignment.threads.max(1) as usize;
    let _ = rayon::ThreadPoolBuilder::new()
        .num_threads(threads)
        .build_global();
    emit_ready(&state);

    let stdin = std::io::stdin();
    for line in stdin.lock().lines() {
        let line = line.map_err(|e| e.to_string())?;
        if line.trim().is_empty() {
            continue;
        }
        let req: Value = match serde_json::from_str(&line) {
            Ok(v) => v,
            Err(e) => {
                emit_json(&json!({"ok":false,"error": format!("invalid JSON: {e}")}));
                continue;
            }
        };
        let (reply, stop) = dispatch(&mut state, &req);
        emit_json(&reply);
        if stop {
            break;
        }
    }
    crate::run_pipeline::flush_preview_encodes();
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn explicit_cube_model_wins_over_last_fit() {
        let state = ServeState {
            last: Some(SavedPipeline::default()),
            ..ServeState::default()
        };
        let reply = cmd_export_cube(
            &state,
            &json!({"model":"definitely-missing-model.json", "path":"unused.cube"}),
        );
        assert_eq!(
            reply["ok"], false,
            "must load the explicit model, not export stale state"
        );
    }

    #[cfg(feature = "video")]
    #[test]
    fn video_lut_honors_requested_stage_switches() {
        let saved = SavedPipeline {
            gc_coeffs: Some(vec![0.0, 1.0]),
            wb_diag: Some([1.2, 0.8, 1.1]),
            ..SavedPipeline::default()
        };
        let apply =
            ApplyOptions::from_req(&json!({"do_gc":false,"do_wb":false,"do_cc":false}), &saved);
        let selected = video_pipeline(&saved, &apply).unwrap();
        let (lut, _) = selected.lut_pipeline().unwrap();
        assert!(lut.gc.is_none() && lut.wb.is_none() && lut.cc.is_none());
        assert_eq!(lut.apply_color([0.2, 0.4, 0.6]).unwrap(), [0.2, 0.4, 0.6]);
        let default = video_pipeline(&saved, &ApplyOptions::from_saved(&saved)).unwrap();
        let (lut, _) = default.lut_pipeline().unwrap();
        assert!(lut.gc.is_some() && lut.wb.is_some());
    }

    #[cfg(feature = "video")]
    #[test]
    fn video_rejects_mislabeled_model_colorimetry() {
        for (input, target, white) in [
            ("linear-srgb", "srgb", "d65"),
            ("srgb", "display-p3", "d65"),
            ("srgb", "srgb", "d50"),
        ] {
            let saved = SavedPipeline {
                input_space_id: input.into(),
                target_space_id: target.into(),
                target_white_id: white.into(),
                do_gc: false,
                do_wb: false,
                ..SavedPipeline::default()
            };
            assert!(video_pipeline(&saved, &ApplyOptions::from_saved(&saved)).is_err());
        }
        let saved = SavedPipeline {
            target_space_id: "display-p3".into(),
            ..SavedPipeline::default()
        };
        let apply = ApplyOptions::from_req(&json!({"do_cc":false}), &saved);
        assert!(
            video_pipeline(&saved, &apply).is_ok(),
            "disabled CC does not emit wide-gamut pixels"
        );
    }

    #[test]
    fn duplicate_stems_get_distinct_stable_artifact_bases() {
        let images = vec![
            "shoot-a/IMG_001.jpg".to_string(),
            "shoot-b/img_001.png".to_string(),
            "shoot-b/unique.tif".to_string(),
        ];
        let bases = unique_artifact_bases(&images);
        assert_eq!(bases, vec!["IMG_001_1", "img_001_2", "unique"]);
    }

    #[test]
    fn devices_ok() {
        let mut state = ServeState::default();
        let (reply, stop) = dispatch(&mut state, &json!({"cmd":"devices","policy":"cpu"}));
        assert!(!stop);
        assert_eq!(reply["ok"], true);
        assert!(reply.get("cpu").is_some());
        assert!(reply.get("backends").is_some());
        assert!(reply.get("assignment").is_some());
        assert_eq!(reply["assignment"]["detect"], "cpu");
        assert_eq!(reply["assignment"]["plane"], "cpu");
        assert_eq!(reply["assignment"]["apply"], "cpu");
    }

    #[test]
    fn shutdown_and_quit() {
        let mut state = ServeState::default();
        let (a, stop_a) = dispatch(&mut state, &json!({"cmd":"shutdown"}));
        assert!(stop_a);
        assert_eq!(a["ok"], true);
        let (b, stop_b) = dispatch(&mut state, &json!({"cmd":"quit"}));
        assert!(stop_b);
        assert_eq!(b["event"], "shutdown");
    }

    #[test]
    fn unknown_cmd() {
        let mut state = ServeState::default();
        let (reply, stop) = dispatch(&mut state, &json!({"cmd":"nope"}));
        assert!(!stop);
        assert_eq!(reply["ok"], false);
    }

    #[test]
    fn detect_cache_key_includes_mtime_crop_prefer() {
        let a = detect_cache_key(Path::new("x.jpg"), 0.75, "");
        let b = detect_cache_key(Path::new("x.jpg"), 0.5, "spyder");
        assert_ne!(a, b);
    }
}
