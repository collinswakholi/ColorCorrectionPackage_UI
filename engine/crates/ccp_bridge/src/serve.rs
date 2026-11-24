//! First-class NDJSON serve (stdin/stdout). Host drains stderr.

use std::collections::{HashMap, HashSet, VecDeque};
use std::io::{BufRead, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::SystemTime;

use ccp_core::lut::DEFAULT_LUT_SIZE;
use ccp_detect::plane::last_plane_ep;
use ccp_detect::{
    detect_chart, probe_accelerators, set_runtime_from_report, warmup_detector, DetectSettings,
    DevicePolicy, DEFAULT_CONF, DEFAULT_CROP,
};
use serde_json::{json, Value};

use crate::error::{Error, ErrorCode};
use crate::run_pipeline::{
    apply_saved_to_image_ex_with_progress, export_cube_from_saved, json_bool, json_f64, json_str,
    last_apply_backend, load_saved, run_from_json_ex, ApplyOptions, FfcGainCache, PreviewMode,
    SavedPipeline,
};

/// Detection cache entries (and their private temp copies) kept per worker.
const DETECT_CACHE_CAPACITY: usize = 64;
/// Worker-chosen default `detect` handshake files kept before the oldest is
/// deleted. All remaining worker temp files are removed when the loop exits.
const TEMP_OUTPUT_CAPACITY: usize = 64;

#[derive(Debug)]
pub struct ServeState {
    pub policy: String,
    pub device: String,
    pub warmed: bool,
    pub last: Option<SavedPipeline>,
    pub detect_cache: HashMap<String, PathBuf>,
    pub ffc_cache: Arc<FfcGainCache>,
    /// Least-recently-used order of `detect_cache` keys (front = oldest).
    pub detect_cache_order: VecDeque<String>,
    /// Default detect handshakes this worker wrote to the temp directory.
    pub temp_outputs: VecDeque<PathBuf>,
}

impl Default for ServeState {
    fn default() -> Self {
        Self {
            policy: "auto".into(),
            device: "pending".into(),
            warmed: false,
            last: None,
            detect_cache: HashMap::new(),
            ffc_cache: Arc::new(FfcGainCache::new()),
            detect_cache_order: VecDeque::new(),
            temp_outputs: VecDeque::new(),
        }
    }
}

fn with_accel(mut value: Value) -> Value {
    if let Some(obj) = value.as_object_mut() {
        obj.insert("apply_backend".into(), json!(last_apply_backend()));
        if let Some(execution) = crate::run_pipeline::last_apply_execution() {
            obj.insert("execution".into(), execution);
        }
        if let Some(plane) = last_plane_ep() {
            obj.insert("plane_backend".into(), json!(plane));
            obj.insert(
                "inference".into(),
                json!({"plane":ccp_detect::plane::last_plane_inference()}),
            );
        }
    }
    value
}

/// NDJSON protocol version announced in the ready event. Version 3 adds a
/// stable `code` to every `ok:false` reply; everything else matches version 2.
pub const PROTOCOL_VERSION: u32 = 3;

fn fail(code: ErrorCode, message: impl Into<String>) -> Value {
    json!({"ok": false, "code": code.as_str(), "error": message.into()})
}

fn fail_with(error: &Error) -> Value {
    fail(error.code(), error.to_string())
}

fn detect_code(result: &ccp_detect::DetectResult) -> ErrorCode {
    ErrorCode::parse(&result.error_code).unwrap_or(ErrorCode::InferenceFailed)
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
    settings.device = policy.as_str().into();
    settings.prefer = json_str(req, &["prefer"]).unwrap_or("").to_string();
    settings.conf = json_f64(req, &["conf"], f64::from(DEFAULT_CONF)) as f32;
    settings.crop = json_f64(req, &["crop"], DEFAULT_CROP);
    settings.allow_recall = json_bool(req, &["allow_recall"], true);
    settings
}

fn detect_cache_key(path: &Path, settings: &DetectSettings) -> String {
    use ccp_detect::cache_key::{model_cache_identity, runtime_cache_identity};
    use ccp_detect::paths::{primary_onnx_path, recall_onnx_path};
    let canonical = path.canonicalize().unwrap_or_else(|_| path.to_path_buf());
    let metadata = std::fs::metadata(path).ok();
    let deploy = if settings.deploy_dir.as_os_str().is_empty() {
        ccp_detect::default_deploy_dir()
    } else {
        settings.deploy_dir.clone()
    };
    let models = [primary_onnx_path(&deploy), recall_onnx_path(&deploy)]
        .iter()
        .map(|p| model_cache_identity(p))
        .collect::<Vec<_>>();
    format!(
        "{}|{:?}|{:?}|{}|{:?}|{}",
        canonical.display(),
        metadata.as_ref().map(|m| m.len()),
        metadata.and_then(|m| m.modified().ok()),
        serde_json::to_string(settings).unwrap_or_default(),
        models,
        runtime_cache_identity()
    )
}

fn identify_detection_handshake(mut handshake: Value) -> Value {
    use std::sync::atomic::{AtomicU64, Ordering};
    static SEQUENCE: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let snapshot = format!(
        "detect-{}-{nanos}-{}",
        std::process::id(),
        SEQUENCE.fetch_add(1, Ordering::Relaxed)
    );
    handshake["snapshot_id"] = json!(snapshot);
    if let Some(charts) = handshake.get_mut("charts").and_then(Value::as_array_mut) {
        for (index, chart) in charts.iter_mut().enumerate() {
            chart["chart_id"] = json!(format!("{snapshot}:chart-{index}"));
            chart["snapshot_id"] = json!(snapshot);
        }
    }
    handshake["chart_id"] = json!(format!("{snapshot}:chart-0"));
    handshake
}

fn ready_title(warmed: bool, policy: &str) -> String {
    #[cfg(feature = "ort-plane")]
    if warmed {
        if let Some(actual) = ccp_detect::last_detect_ep() {
            return format!("Warmed · {actual} inference");
        }
    }
    let report = probe_accelerators(policy);
    let warm = if warmed { "Warmed" } else { "Ready" };
    format!("{warm} · {}", report.recommended)
}

fn ready_event(state: &ServeState) -> Value {
    json!({
        "ok": true,
        "event": "ready",
        "patch_sampling": "random-lcg32-v1",
        "device": state.device,
        "warmed": state.warmed,
        "title": ready_title(state.warmed, &state.policy),
        "protocol_version": PROTOCOL_VERSION,
        "capabilities": ["custom_references_v1", "chart_identity_v1", "combined_lut65_v1", "cpu_inference_v1"],
        "portable_lut": cfg!(feature = "wgpu-lut"),
    })
}

fn emit_ready(state: &ServeState) {
    emit_json(&ready_event(state));
}

fn cmd_devices(state: &ServeState, req: &Value) -> Value {
    let policy = policy_from_req(req, &state.policy);
    let report = probe_accelerators(&policy);
    set_runtime_from_report(&report);
    let mut v = serde_json::to_value(&report).unwrap_or_else(|_| json!({}));
    if let Some(obj) = v.as_object_mut() {
        obj.insert("ok".into(), json!(true));
        #[cfg(feature = "ort-plane")]
        if state.warmed {
            let inference = ccp_detect::infer_ort::inference_records(false);
            if inference
                .pointer("/detect/requested_policy")
                .and_then(Value::as_str)
                == Some(policy.as_str())
            {
                obj.insert("verified_inference".into(), inference);
            }
        }
    }
    v
}

fn cmd_warmup(state: &mut ServeState, req: &Value) -> Value {
    emit_progress("Warming Opt2 1280");
    state.policy = policy_from_req(req, &state.policy);
    set_runtime_from_report(&probe_accelerators(&state.policy));
    let settings = detect_settings(state, req);
    let result = warmup_detector(&settings);
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
            "detect_backend":result.device,
            "inference": result.inference,
        })
    } else {
        json!({
            "ok": false,
            "code": detect_code(&result).as_str(),
            "warmed": false,
            "device": state.device,
            "error": result.error,
        })
    }
}

fn cmd_setup_probe(state: &mut ServeState, req: &Value) -> Value {
    let inference = cmd_warmup(state, req);
    let mut lut = Vec::new();
    for b in 0..2 {
        for g in 0..2 {
            for r in 0..2 {
                lut.extend_from_slice(&[r as f32, g as f32, b as f32]);
            }
        }
    }
    let mut pixels = vec![0.25, 0.5, 0.75];
    let cpu = json_bool(req, &["force_cpu"], false);
    let correction = if cpu {
        ccp_detect::lut_chunks::apply_chunks(&mut pixels, &lut, 2, 1, |_| {
            Err("CPU requested".into())
        })
        .map(|_| json!({"status":"verified","backend":"cpu","fallback":null}))
    } else {
        ccp_detect::lut_wgpu::apply(&mut pixels,&lut,2)
            .map(|actual|json!({"status":"verified","backend":actual.backend,"fallback":actual.chunks.fallback_reason}))
    };
    let correction = correction.unwrap_or_else(|reason| json!({"status":"failed","error":reason}));
    let verified = inference["ok"] == true && correction["status"] == "verified";
    let mut reply = json!({"ok":verified, "setup_verified":verified,
        "inference":inference,"correction":correction});
    if !verified {
        let (code, error) = if inference["ok"] != true {
            (
                inference["code"]
                    .as_str()
                    .unwrap_or(ErrorCode::InferenceFailed.as_str())
                    .to_string(),
                inference["error"]
                    .as_str()
                    .unwrap_or("inference setup failed")
                    .to_string(),
            )
        } else {
            (
                ErrorCode::ProcessingFailed.as_str().to_string(),
                correction["error"]
                    .as_str()
                    .unwrap_or("correction setup failed")
                    .to_string(),
            )
        };
        reply["code"] = json!(code);
        reply["error"] = json!(error);
    }
    reply
}

fn cmd_detect(state: &mut ServeState, req: &Value) -> Value {
    let image = match json_str(req, &["image", "input", "image_path"]) {
        Some(p) => PathBuf::from(p),
        None => return fail(ErrorCode::InvalidRequest, "detect requires image"),
    };
    if !image.is_file() {
        return fail(
            ErrorCode::IoError,
            format!("image not found: {}", image.display()),
        );
    }
    // Fresh SDK clients need no preceding devices/warmup request. Routing in
    // ccp_detect reads the runtime assignment, so synchronize it before both
    // cache identity construction and inference, just as run/apply already do.
    state.policy = policy_from_req(req, &state.policy);
    sync_runtime(state, req);
    let settings = detect_settings(state, req);
    if json_bool(req, &["retry_inference"], false) {
        #[cfg(feature = "ort-plane")]
        ccp_detect::reset_inference_sessions();
        clear_detect_cache(state);
    }
    let key = detect_cache_key(&image, &settings);
    let bypass_cache = json_bool(req, &["bypass_cache"], false);
    if let Some(cached) = state
        .detect_cache
        .get(&key)
        .cloned()
        .filter(|_| !bypass_cache)
    {
        let cached = &cached;
        if cached.is_file() {
            if let Ok(text) = std::fs::read_to_string(cached) {
                if !text.trim().is_empty() {
                    if let Ok(hs) = serde_json::from_str::<Value>(&text) {
                        // Callers may reuse a requested destination for different
                        // settings. Cache entries point to immutable private copies.
                        let requested =
                            json_str(req, &["handshake_out", "json_out"]).map(PathBuf::from);
                        let returned = requested.as_ref().unwrap_or(cached);
                        if returned != cached {
                            if let Err(error) = write_with_parent(returned, text.as_bytes()) {
                                return fail(ErrorCode::IoError, error);
                            }
                        }
                        touch_detect_cache(state, &key);
                        return json!({
                            "ok": true,
                            "handshake": returned,
                            "n_charts": hs.get("n_charts").cloned().unwrap_or(json!(1)),
                            "chart_type": hs.get("chart_type").cloned().unwrap_or(json!("")),
                            "device": hs.get("device"),
                            "detect_backend": hs.get("device"),
                            "provider": hs.pointer("/inference/detect/provider"),
                            "fallback": hs.pointer("/inference/detect/fallback"),
                            "inference": hs.get("inference"),
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
            "code": detect_code(&result).as_str(),
            "error": result.error,
            "device":result.device,
            "detect_backend":result.device,
            "inference":result.inference,
            "n_charts": result.n_charts,
            "chart_type": result.chart_type,
        });
    }
    if result.json.trim().is_empty() {
        return json!({
            "ok": false,
            "code": ErrorCode::InferenceFailed.as_str(),
            "error": "detect produced an empty handshake",
            "n_charts": result.n_charts,
            "chart_type": result.chart_type,
        });
    }
    let handshake = match serde_json::from_str::<Value>(&result.json) {
        Ok(value) => identify_detection_handshake(value),
        Err(error) => {
            return fail(
                ErrorCode::InferenceFailed,
                format!("invalid detection handshake: {error}"),
            )
        }
    };
    let handshake_out = json_str(req, &["handshake_out", "json_out"]).map(PathBuf::from);
    let worker_owned = handshake_out.is_none();
    let out = handshake_out.unwrap_or_else(|| {
        let mut p = std::env::temp_dir();
        let stem = image
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("detect");
        p.push(format!(
            "ccp-detect-{stem}-{}.json",
            handshake["snapshot_id"].as_str().unwrap_or("snapshot")
        ));
        p
    });
    if let Err(e) = write_with_parent(&out, &serde_json::to_vec(&handshake).unwrap_or_default()) {
        return fail(ErrorCode::IoError, e);
    }
    if worker_owned {
        remember_temp_output(state, out.clone());
    }
    if !bypass_cache {
        let cache_path = std::env::temp_dir().join(format!(
            "ccp-cache-{}.json",
            handshake["snapshot_id"].as_str().unwrap_or("snapshot")
        ));
        if std::fs::write(
            &cache_path,
            serde_json::to_vec(&handshake).unwrap_or_default(),
        )
        .is_ok()
        {
            remember_detect_cache(state, key, cache_path);
        }
    }
    if !result.device.is_empty() {
        state.device = result.device.clone();
    }
    json!({
        "ok": true,
        "handshake": out,
        "n_charts": result.n_charts,
        "chart_type": result.chart_type,
        "device": result.device,
        "detect_backend":result.device,
        "provider":result.inference.pointer("/detect/provider"),
        "fallback":result.inference.pointer("/detect/fallback"),
        "inference":result.inference,
        "recall_source": result.recall_source,
        "cached": false,
    })
}

fn write_with_parent(path: &Path, bytes: &[u8]) -> Result<(), String> {
    if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
        std::fs::create_dir_all(parent)
            .map_err(|e| format!("cannot create {}: {e}", parent.display()))?;
    }
    std::fs::write(path, bytes).map_err(|e| format!("cannot write {}: {e}", path.display()))
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
        Err(e) => fail_with(&e),
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
        *totals.entry(stem.to_lowercase()).or_default() += 1;
    }
    // Reserve every literal stem before allocating duplicate suffixes. Otherwise
    // a.jpg, a.png and a_1.jpg would overwrite the same a_1 output/preview.
    let reserved: HashSet<String> = totals.keys().cloned().collect();
    let mut allocated = HashSet::<String>::new();
    let mut occurrences = HashMap::<String, usize>::new();
    stems
        .into_iter()
        .map(|stem| {
            let key = stem.to_lowercase();
            if totals.get(&key).copied().unwrap_or(0) <= 1 {
                allocated.insert(key);
                return stem;
            }
            let occurrence = occurrences.entry(key).or_default();
            loop {
                *occurrence += 1;
                let candidate = format!("{stem}_{}", *occurrence);
                let candidate_key = candidate.to_lowercase();
                if !reserved.contains(&candidate_key) && allocated.insert(candidate_key) {
                    break candidate;
                }
            }
        })
        .collect()
}

fn cmd_apply(state: &ServeState, req: &Value) -> Value {
    sync_runtime(state, req);
    let model_path = match json_str(req, &["model", "model_path", "model_in"]) {
        Some(p) => PathBuf::from(p),
        None => return fail(ErrorCode::InvalidRequest, "apply requires model"),
    };
    let saved = match load_saved(&model_path) {
        Ok(s) => s,
        Err(e) => return fail_with(&e),
    };
    let images = req
        .get("images")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    if images.is_empty() {
        return fail(ErrorCode::InvalidRequest, "apply requires images");
    }
    let output_dir = match json_str(req, &["output_dir", "output"]) {
        Some(p) => PathBuf::from(p),
        None => return fail(ErrorCode::InvalidRequest, "apply requires output_dir"),
    };
    let output_ext = match req.get("output_format") {
        None => "jpg",
        Some(v) => match v.as_str().map(str::to_ascii_lowercase).as_deref() {
            Some("png") => "png",
            Some("jpg" | "jpeg") => "jpg",
            _ => {
                return fail(
                    ErrorCode::InvalidRequest,
                    "output_format must be png, jpg or jpeg",
                )
            }
        },
    };
    let mut valid_images = Vec::with_capacity(images.len());
    for (index, value) in images.iter().enumerate() {
        match value.as_str().filter(|s| !s.is_empty()) {
            Some(image) => valid_images.push(image.to_string()),
            None => {
                return fail(
                    ErrorCode::InvalidRequest,
                    format!("images[{index}] must be a nonempty path string"),
                )
            }
        }
    }
    if let Err(e) = std::fs::create_dir_all(&output_dir) {
        return fail(
            ErrorCode::IoError,
            format!("cannot create output_dir {}: {e}", output_dir.display()),
        );
    }
    emit_progress(&format!("Applying model to {} images", images.len()));

    let preview_root = json_str(req, &["preview_dir"]).map(PathBuf::from);
    let artifact_bases = unique_artifact_bases(&valid_images);
    let mut jobs: Vec<(String, PathBuf, Option<PathBuf>)> = Vec::with_capacity(images.len());
    for (img, artifact_base) in valid_images.into_iter().zip(artifact_bases) {
        let dest = output_dir.join(format!("{artifact_base}_corrected.{output_ext}"));
        let preview = match preview_root.as_ref() {
            None => None,
            Some(root) => {
                let dir = if images.len() > 1 {
                    root.join(&artifact_base)
                } else {
                    root.clone()
                };
                if let Err(e) = std::fs::create_dir_all(&dir) {
                    return fail(
                        ErrorCode::IoError,
                        format!("cannot create preview_dir {}: {e}", dir.display()),
                    );
                }
                Some(dir)
            }
        };
        jobs.push((img, dest, preview));
    }

    let mut apply = match ApplyOptions::try_from_req(req, &saved) {
        Ok(apply) => apply,
        Err(e) => return fail_with(&e),
    };
    apply.ffc_cache = Some(state.ffc_cache.clone());
    let apply_one = |img: &str, out: &Path, preview: Option<&Path>| -> Value {
        emit_progress(&format!(
            "Apply · {}",
            Path::new(img)
                .file_name()
                .and_then(|s| s.to_str())
                .unwrap_or(img)
        ));
        match apply_saved_to_image_ex_with_progress(
            &saved,
            Path::new(img),
            out,
            preview,
            &apply,
            &mut emit_progress,
        ) {
            Ok(report) => {
                let mut row = json!({
                    "ok": true,
                    "image": img,
                    "output": out,
                    "preview_dir": preview,
                    "metrics_hidden": true,
                    "timings": {
                        "decode_seconds": report.decode_seconds,
                        "processing_seconds": report.processing_seconds,
                        "export_seconds": report.export_seconds,
                    },
                });
                if let Some(skip) = report.ffc_skipped {
                    row["ffc_skipped"] = json!(skip);
                }
                with_accel(row)
            }
            Err(e) => json!({
                "ok": false,
                "code": e.code().as_str(),
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
    let mut reply = json!({
        "ok": failed < results.len(),
        "results": results,
        "metrics_hidden": true,
        "error": if failed > 0 {
            Some(format!("{failed} of {} images failed", results.len()))
        } else {
            None
        },
    });
    if reply["ok"] == false {
        // Every image failed: report the first image's code for the batch.
        let first = reply["results"]
            .as_array()
            .and_then(|rows| rows.iter().find_map(|row| row["code"].as_str()))
            .unwrap_or(ErrorCode::ProcessingFailed.as_str())
            .to_string();
        reply["code"] = json!(first);
    }
    with_accel(reply)
}

fn cmd_preview_stage(state: &ServeState, req: &Value) -> Value {
    sync_runtime(state, req);
    let model_path = match json_str(req, &["model", "model_path", "model_in"]) {
        Some(p) => PathBuf::from(p),
        None => return fail(ErrorCode::InvalidRequest, "preview_stage requires model"),
    };
    let saved = match load_saved(&model_path) {
        Ok(s) => s,
        Err(e) => return fail_with(&e),
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
                None => return fail(ErrorCode::InvalidRequest, "preview_stage requires image"),
            }
        }
    };
    let preview_dir = match json_str(req, &["preview_dir"]) {
        Some(p) => PathBuf::from(p),
        None => {
            return fail(
                ErrorCode::InvalidRequest,
                "preview_stage requires preview_dir",
            )
        }
    };
    let stage = json_str(req, &["stage", "preview_mode"])
        .unwrap_or("corrected")
        .to_ascii_lowercase();
    let dummy = preview_dir.join("_unused.jpg");
    let mut apply = match ApplyOptions::try_from_req(req, &saved) {
        Ok(apply) => apply,
        Err(e) => return fail_with(&e),
    };
    apply.write_output = false;
    apply.preview_mode = PreviewMode::Stage(stage.clone());
    // Intermediate previews expose the original stage math. Fast mode only
    // approximates a final rendered image, not the inspection of a stage.
    if matches!(stage.as_str(), "ffc" | "gc" | "wb") {
        apply.fast_mode = false;
    }
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
        other => return fail(ErrorCode::InvalidRequest, format!("unknown stage: {other}")),
    }
    match apply_saved_to_image_ex_with_progress(
        &saved,
        &image,
        &dummy,
        Some(&preview_dir),
        &apply,
        &mut emit_progress,
    ) {
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
        Err(e) => fail_with(&e),
    }
}

fn cmd_export_cube(state: &ServeState, req: &Value) -> Value {
    // An explicit model must win over the last fit in a long-lived API session.
    if let Some(model) = json_str(req, &["model", "model_path"]) {
        return match load_saved(model) {
            Ok(saved) => write_cube_reply(&saved, req),
            Err(e) => fail_with(&e),
        };
    }
    let Some(saved) = &state.last else {
        return fail(
            ErrorCode::InvalidRequest,
            "export_cube needs a prior run or model",
        );
    };
    write_cube_reply(saved, req)
}

fn write_cube_reply(saved: &SavedPipeline, req: &Value) -> Value {
    let path = match json_str(req, &["path", "output", "lut_out"]) {
        Some(p) => PathBuf::from(p),
        None => return fail(ErrorCode::InvalidRequest, "export_cube requires path"),
    };
    let size = match req.get("size") {
        None => DEFAULT_LUT_SIZE,
        Some(value) => match value.as_u64().filter(|n| (2..=129).contains(n)) {
            Some(size) => size as usize,
            None => {
                return fail(
                    ErrorCode::InvalidRequest,
                    "size must be an integer from 2 to 129",
                )
            }
        },
    };
    match export_cube_from_saved(saved, &path, size) {
        Ok(()) => {
            json!({"ok":true,"path": path, "size": size, "note":"LUT does not include flat-field."})
        }
        Err(e) => fail_with(&e),
    }
}

pub fn dispatch(state: &mut ServeState, req: &Value) -> (Value, bool) {
    // Reset per request so a preview or previous batch cannot leak its choice.
    crate::run_pipeline::set_auto_lut_request(false);
    crate::run_pipeline::set_cpu_lut_request(json_bool(req, &["cpu_lut"], false));
    if let Some(mode) = req.get("lut_device") {
        match mode.as_str() {
            Some("auto") => crate::run_pipeline::set_auto_lut_request(true),
            Some("cpu") => crate::run_pipeline::set_cpu_lut_request(true),
            Some("gpu") => {}
            _ => {
                return (
                    fail(
                        ErrorCode::InvalidRequest,
                        "lut_device must be auto, cpu, or gpu",
                    ),
                    false,
                )
            }
        }
    }
    let cmd = json_str(req, &["cmd", "command"])
        .unwrap_or("")
        .to_ascii_lowercase();
    if req.get("cc_method").is_some() {
        return (
            fail(
                ErrorCode::InvalidRequest,
                "cc_method was removed; use fast_mode: true for the optional combined LUT",
            ),
            false,
        );
    }
    if let Some(value) = req.get("fast_mode") {
        if !value.is_boolean() || !matches!(cmd.as_str(), "run" | "apply" | "preview_stage") {
            return (
                fail(
                    ErrorCode::InvalidRequest,
                    "fast_mode must be a boolean on run/apply/preview_stage requests",
                ),
                false,
            );
        }
    }
    if matches!(cmd.as_str(), "run" | "apply" | "preview_stage") {
        ccp_detect::plane::reset_plane_execution();
    }
    match cmd.as_str() {
        "devices" => (cmd_devices(state, req), false),
        "setup_probe" => (cmd_setup_probe(state, req), false),
        "warmup" => (cmd_warmup(state, req), false),
        "detect" => (cmd_detect(state, req), false),
        "run" => (cmd_run(state, req), false),
        "apply" => (cmd_apply(state, req), false),
        "preview_stage" => (cmd_preview_stage(state, req), false),
        "export_cube" => (cmd_export_cube(state, req), false),
        "video" | "video_grade" => (
            fail(
                ErrorCode::UnsupportedCommand,
                "Video processing has been removed; this app supports still images only.",
            ),
            false,
        ),
        #[cfg(test)]
        "__test_panic" => panic!("deliberate test panic"),
        "shutdown" | "quit" => {
            let _ = crate::run_pipeline::flush_preview_encodes();
            (json!({"ok":true,"event":"shutdown"}), true)
        }
        "" => (fail(ErrorCode::InvalidRequest, "missing cmd"), false),
        other => (
            fail(
                ErrorCode::UnsupportedCommand,
                format!("unknown cmd: {other}"),
            ),
            false,
        ),
    }
}

fn panic_message(payload: &(dyn std::any::Any + Send)) -> String {
    payload
        .downcast_ref::<&str>()
        .map(|s| s.to_string())
        .or_else(|| payload.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "unknown panic".into())
}

/// [`dispatch`] behind a panic boundary: a bug in one request becomes an
/// error reply, so the worker stays alive and the reply stream stays in sync.
pub fn dispatch_guarded(state: &mut ServeState, req: &Value) -> (Value, bool) {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| dispatch(state, req)));
    match result {
        Ok(reply) => reply,
        Err(payload) => {
            // Preview encoders may still reference a half-written request.
            let _ = crate::run_pipeline::flush_preview_encodes();
            (
                fail(
                    ErrorCode::InternalError,
                    format!("internal error: {}", panic_message(payload.as_ref())),
                ),
                false,
            )
        }
    }
}

/// Long-lived stdin/stdout loop. First line is the ready event.
pub fn run() -> Result<(), String> {
    let mut state = ServeState {
        ..ServeState::default()
    };
    let report = probe_accelerators(&state.policy);
    set_runtime_from_report(&report);
    state.device = "pending".into();
    let threads = report.assignment.threads.max(1) as usize;
    let _ = rayon::ThreadPoolBuilder::new()
        .num_threads(threads)
        .build_global();
    emit_ready(&state);

    let stdin = std::io::stdin();
    let result = serve_lines(&mut state, stdin.lock(), &mut |reply| emit_json(reply));
    let _ = crate::run_pipeline::flush_preview_encodes();
    cleanup_temp_files(&mut state);
    result
}

/// Read NDJSON requests until EOF or shutdown, emitting one reply per request.
pub fn serve_lines<R: BufRead>(
    state: &mut ServeState,
    reader: R,
    emit: &mut dyn FnMut(&Value),
) -> Result<(), String> {
    let mut reader = reader;
    let mut buffer = Vec::new();
    loop {
        buffer.clear();
        if reader
            .read_until(b'\n', &mut buffer)
            .map_err(|e| e.to_string())?
            == 0
        {
            break;
        }
        let line = match std::str::from_utf8(&buffer) {
            Ok(line) => line,
            Err(e) => {
                emit(&fail(
                    ErrorCode::InvalidRequest,
                    format!("invalid UTF-8 in request: {e}"),
                ));
                continue;
            }
        };
        if line.trim().is_empty() {
            continue;
        }
        let req: Value = match serde_json::from_str(line) {
            Ok(v) => v,
            Err(e) => {
                emit(&fail(
                    ErrorCode::InvalidRequest,
                    format!("invalid JSON: {e}"),
                ));
                continue;
            }
        };
        let (reply, stop) = dispatch_guarded(state, &req);
        emit(&reply);
        if stop {
            break;
        }
    }
    Ok(())
}

fn touch_detect_cache(state: &mut ServeState, key: &str) {
    if let Some(index) = state.detect_cache_order.iter().position(|k| k == key) {
        if let Some(key) = state.detect_cache_order.remove(index) {
            state.detect_cache_order.push_back(key);
        }
    }
}

fn remember_detect_cache(state: &mut ServeState, key: String, path: PathBuf) {
    if let Some(previous) = state.detect_cache.insert(key.clone(), path.clone()) {
        if previous != path {
            let _ = std::fs::remove_file(previous);
        }
        state.detect_cache_order.retain(|k| k != &key);
    }
    state.detect_cache_order.push_back(key);
    while state.detect_cache.len() > DETECT_CACHE_CAPACITY {
        let Some(oldest) = state.detect_cache_order.pop_front() else {
            break;
        };
        if let Some(evicted) = state.detect_cache.remove(&oldest) {
            let _ = std::fs::remove_file(evicted);
        }
    }
}

fn clear_detect_cache(state: &mut ServeState) {
    for (_, path) in state.detect_cache.drain() {
        let _ = std::fs::remove_file(path);
    }
    state.detect_cache_order.clear();
}

fn remember_temp_output(state: &mut ServeState, path: PathBuf) {
    state.temp_outputs.retain(|p| p != &path);
    state.temp_outputs.push_back(path);
    while state.temp_outputs.len() > TEMP_OUTPUT_CAPACITY {
        if let Some(oldest) = state.temp_outputs.pop_front() {
            let _ = std::fs::remove_file(oldest);
        }
    }
}

/// Remove every temp file this worker created (detect cache copies and
/// default handshake outputs). Caller-chosen `handshake_out` paths are kept.
fn cleanup_temp_files(state: &mut ServeState) {
    clear_detect_cache(state);
    for path in state.temp_outputs.drain(..) {
        let _ = std::fs::remove_file(path);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn invalid_or_unsupported_cc_method_fails_before_work() {
        let mut state = ServeState::default();
        for req in [
            json!({"cmd":"apply","cc_method":"typo"}),
            json!({"cmd":"apply","cc_method":false}),
            json!({"cmd":"run","cc_method":"direct"}),
        ] {
            let (reply, stop) = dispatch(&mut state, &req);
            assert!(!stop);
            assert_eq!(reply["ok"], false);
            assert!(reply["error"].as_str().unwrap().contains("cc_method"));
        }
    }

    #[test]
    fn cube_export_rejects_invalid_sizes_before_writing() {
        let dir = std::env::temp_dir().join(format!("ccp-invalid-cube-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("invalid.cube");
        for size in [
            json!(true),
            json!(0),
            json!(1),
            json!(-2),
            json!(33.5),
            json!(130),
            json!("33"),
            Value::Null,
            json!(u64::MAX),
        ] {
            let reply =
                write_cube_reply(&SavedPipeline::default(), &json!({"path":path,"size":size}));
            assert_eq!(reply["ok"], false);
            assert!(reply["error"].as_str().unwrap().contains("size"));
            assert!(!path.exists());
        }
        std::fs::remove_dir_all(dir).unwrap();
    }

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
    fn generated_artifact_suffixes_do_not_overwrite_literal_stems() {
        for (images, expected) in [
            (vec!["a.jpg", "a.png", "a_1.jpg"], vec!["a_2", "a_3", "a_1"]),
            (vec!["a_1.jpg", "a.jpg", "a.png"], vec!["a_1", "a_2", "a_3"]),
            (
                vec!["a.jpg", "a.png", "A_1.jpg", "a_1.png", "a_1_1.jpg"],
                vec!["a_2", "a_3", "A_1_2", "a_1_3", "a_1_1"],
            ),
            (
                vec!["a.jpg", "a.jpg", "a_1.jpg", "a_2.jpg"],
                vec!["a_3", "a_4", "a_1", "a_2"],
            ),
            (vec!["Ä.jpg", "ä.png", "Ä_1.jpg"], vec!["Ä_2", "ä_3", "Ä_1"]),
        ] {
            let images = images.into_iter().map(String::from).collect::<Vec<_>>();
            let bases = unique_artifact_bases(&images);
            assert_eq!(bases, expected);
            assert_eq!(
                bases
                    .iter()
                    .map(|v| v.to_lowercase())
                    .collect::<HashSet<_>>()
                    .len(),
                images.len()
            );
        }
    }

    #[test]
    fn devices_ok() {
        let mut state = ServeState::default();
        let auto = probe_accelerators("auto");
        let (reply, stop) = dispatch(&mut state, &json!({"cmd":"devices","policy":"cpu"}));
        assert!(!stop);
        assert_eq!(reply["ok"], true);
        assert!(reply.get("cpu").is_some());
        assert!(reply.get("backends").is_some());
        assert!(reply.get("assignment").is_some());
        // Legacy cpu selects correction; detection keeps Auto's plan, which
        // can legitimately be CPU in a CPU-only installation.
        assert_eq!(reply["assignment"]["detect"], auto.assignment.detect);
        assert_eq!(reply["assignment"]["plane"], auto.assignment.plane);
        assert_eq!(reply["assignment"]["apply"], "cpu");
    }

    #[test]
    fn fresh_detect_honors_policy_without_devices_or_warmup() {
        let auto = probe_accelerators("auto");
        let mut prior = probe_accelerators("cpu");
        prior.policy = "webgpu".into();
        prior.assignment.detect = "webgpu".into();
        prior.assignment.apply = "webgpu".into();
        set_runtime_from_report(&prior);
        let mut state = ServeState {
            policy: "webgpu".into(),
            device: "webgpu".into(),
            ..ServeState::default()
        };
        // Existing but invalid input reaches routing without a model, fixture,
        // successful inference, or any accelerator-dependent test prerequisite.
        let path = std::env::temp_dir().join(format!(
            "ccp-fresh-detect-policy-{}.invalid",
            std::process::id()
        ));
        std::fs::write(&path, b"not an image").unwrap();
        let (reply, stop) = dispatch(
            &mut state,
            &json!({
                "cmd":"detect", "image":path, "policy":"cpu", "bypass_cache":true
            }),
        );
        let _ = std::fs::remove_file(path);
        assert!(!stop);
        assert_eq!(reply["ok"], false);
        assert_eq!(state.policy, "cpu");
        assert_eq!(ccp_detect::runtime_policy().as_str(), "cpu");
        assert_eq!(
            ccp_detect::runtime_assignment().detect,
            auto.assignment.detect
        );
        assert_eq!(
            ccp_detect::runtime_assignment().plane,
            auto.assignment.plane
        );
        assert_eq!(ccp_detect::runtime_assignment().apply, "cpu");
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
    fn panicking_command_returns_internal_error_and_worker_keeps_serving() {
        let mut state = ServeState::default();
        let (reply, stop) = dispatch_guarded(&mut state, &json!({"cmd":"__test_panic"}));
        assert!(!stop);
        assert_eq!(reply["ok"], false);
        assert!(reply["error"].as_str().unwrap().contains("internal error"));
        let (next, stop) = dispatch_guarded(&mut state, &json!({"cmd":"nope"}));
        assert!(!stop);
        assert!(next["error"].as_str().unwrap().contains("unknown cmd"));
    }

    #[test]
    fn crafted_model_is_rejected_by_apply_without_panicking() {
        let dir = std::env::temp_dir().join(format!("ccp-serve-crafted-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let model = dir.join("model.json");
        std::fs::write(
            &model,
            r#"{"version":2,"kind":"rpcc2","do_ffc":false,"do_gc":false,"do_wb":false,
            "do_cc":true,"gc_coeffs":null,"wb_diag":null,"input_space_id":"srgb",
            "input_white_id":"d65","target_space_id":"srgb","target_white_id":"d65",
            "gain_map_path":null,"correction":{"kind":"rpcc2","feature_count":6,
            "poly_degree":2,"include_combinations":true,"pls_components":0,"hidden_size":0,
            "input_space_id":"srgb","target_space_id":"srgb","target_white_id":"d65",
            "saturated_patches":0,"saturated_names":[],"x_mean":[],"x_std":[],
            "y_mean":[0,0,0],"y_std":[1,1,1],"intercept":[0,0,0],"coefficients":[1.0],
            "w1":[],"b1":[],"w2":[],"b2":[]}}"#,
        )
        .unwrap();
        let mut state = ServeState::default();
        let (reply, stop) = dispatch_guarded(
            &mut state,
            &json!({"cmd":"apply","policy":"cpu","model":model,"images":["x.jpg"],"output_dir":dir}),
        );
        let _ = std::fs::remove_dir_all(&dir);
        assert!(!stop);
        assert_eq!(reply["ok"], false);
        assert!(
            reply["error"]
                .as_str()
                .unwrap()
                .contains("invalid correction model"),
            "{reply}"
        );
    }

    #[test]
    fn invalid_utf8_line_gets_error_reply_and_loop_continues() {
        let mut state = ServeState::default();
        let input = b"\xff\xfe{\"cmd\":\"devices\"}\n{\"cmd\":\"nope\"}\n{\"cmd\":\"shutdown\"}\n";
        let mut replies = Vec::new();
        let result = serve_lines(&mut state, std::io::Cursor::new(&input[..]), &mut |reply| {
            replies.push(reply.clone())
        });
        assert!(result.is_ok(), "{result:?}");
        assert_eq!(replies.len(), 3, "{replies:?}");
        assert_eq!(replies[0]["ok"], false);
        assert!(replies[0]["error"].as_str().unwrap().contains("UTF-8"));
        assert!(replies[1]["error"]
            .as_str()
            .unwrap()
            .contains("unknown cmd"));
        assert_eq!(replies[2]["event"], "shutdown");
    }

    fn valid_model(dir: &Path) -> PathBuf {
        let saved = SavedPipeline {
            do_gc: false,
            do_wb: false,
            correction: Some(ccp_core::CorrectionModel {
                kind: ccp_core::ModelKind::Ccm3x3,
                feature_count: 3,
                coefficients: vec![1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0],
                ..ccp_core::CorrectionModel::default()
            }),
            ..SavedPipeline::default()
        };
        let path = dir.join("model.json");
        std::fs::write(&path, serde_json::to_string(&saved).unwrap()).unwrap();
        path
    }

    #[test]
    fn apply_rejects_non_string_images_and_unwritable_output_dir_before_work() {
        let dir = std::env::temp_dir().join(format!("ccp-serve-apply-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let model = valid_model(&dir);
        let mut state = ServeState::default();
        let (reply, _) = dispatch(
            &mut state,
            &json!({"cmd":"apply","policy":"cpu","model":model,"images":["a.jpg", 5],"output_dir":dir}),
        );
        assert_eq!(reply["ok"], false);
        assert!(
            reply["error"].as_str().unwrap().contains("images[1]"),
            "{reply}"
        );
        assert!(reply.get("results").is_none(), "{reply}");

        let blocker = dir.join("plain-file");
        std::fs::write(&blocker, b"x").unwrap();
        let (reply, _) = dispatch(
            &mut state,
            &json!({"cmd":"apply","policy":"cpu","model":model,"images":["a.jpg"],
                "output_dir":blocker.join("out")}),
        );
        assert_eq!(reply["ok"], false);
        assert!(
            reply["error"].as_str().unwrap().contains("output_dir"),
            "{reply}"
        );
        let (reply, _) = dispatch(
            &mut state,
            &json!({"cmd":"apply","policy":"cpu","model":model,"images":["a.jpg"],"output_dir":dir,
                "preview_dir":blocker.join("previews")}),
        );
        assert_eq!(reply["ok"], false);
        assert!(
            reply["error"].as_str().unwrap().contains("preview_dir"),
            "{reply}"
        );
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn detect_cache_and_temp_outputs_are_bounded_and_cleaned_up() {
        let dir = std::env::temp_dir().join(format!("ccp-serve-cache-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let mut state = ServeState::default();
        let mut cache_files = Vec::new();
        let mut outputs = Vec::new();
        for index in 0..(DETECT_CACHE_CAPACITY + 5) {
            let cached = dir.join(format!("cache-{index}.json"));
            std::fs::write(&cached, b"{}").unwrap();
            remember_detect_cache(&mut state, format!("key-{index}"), cached.clone());
            cache_files.push(cached);
            let output = dir.join(format!("detect-{index}.json"));
            std::fs::write(&output, b"{}").unwrap();
            remember_temp_output(&mut state, output.clone());
            outputs.push(output);
        }
        assert_eq!(state.detect_cache.len(), DETECT_CACHE_CAPACITY);
        assert!(!state.detect_cache.contains_key("key-0"));
        assert!(!cache_files[4].exists() && cache_files[5].exists());
        assert!(!outputs[4].exists() && outputs[5].exists());
        // Re-inserting a key replaces (and removes) its previous private copy.
        let replacement = dir.join("replacement.json");
        std::fs::write(&replacement, b"{}").unwrap();
        remember_detect_cache(&mut state, "key-10".into(), replacement.clone());
        assert!(!cache_files[10].exists());
        assert_eq!(state.detect_cache.len(), DETECT_CACHE_CAPACITY);
        cleanup_temp_files(&mut state);
        assert!(state.detect_cache.is_empty());
        assert!(cache_files.iter().chain(&outputs).all(|p| !p.exists()));
        assert!(!replacement.exists());
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn ready_event_announces_protocol_three() {
        let ready = ready_event(&ServeState::default());
        assert_eq!(ready["protocol_version"], 3);
        assert_eq!(ready["event"], "ready");
        assert!(ready.get("code").is_none());
    }

    #[test]
    fn every_failed_reply_carries_a_stable_code() {
        let dir = std::env::temp_dir().join(format!("ccp-serve-codes-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let model = valid_model(&dir);
        let crafted = dir.join("crafted.json");
        std::fs::write(
            &crafted,
            r#"{"kind":"ccm","feature_count":3,"poly_degree":2,
            "include_combinations":true,"pls_components":0,"hidden_size":0,
            "input_space_id":"srgb","target_space_id":"srgb","target_white_id":"d65",
            "saturated_patches":0,"saturated_names":[],"x_mean":[],"x_std":[],
            "y_mean":[0,0,0],"y_std":[1,1,1],"intercept":[0,0,0],"coefficients":[1],
            "w1":[],"b1":[],"w2":[],"b2":[]}"#,
        )
        .unwrap();
        let not_json = dir.join("not-json.json");
        std::fs::write(&not_json, b"not json").unwrap();
        let not_image = dir.join("not-image.png");
        std::fs::write(&not_image, b"definitely not a png").unwrap();
        let missing = dir.join("missing-model.json");
        let cases = [
            (json!({}), "invalid_request"),
            (json!({"cmd":"nope"}), "unsupported_command"),
            (json!({"cmd":"video"}), "unsupported_command"),
            (
                json!({"cmd":"apply","lut_device":"cuda"}),
                "invalid_request",
            ),
            (json!({"cmd":"run","cc_method":"x"}), "invalid_request"),
            (json!({"cmd":"apply","policy":"cpu"}), "invalid_request"),
            (
                json!({"cmd":"apply","policy":"cpu","model":missing,"images":["a.jpg"],
                "output_dir":dir}),
                "io_error",
            ),
            (
                json!({"cmd":"apply","policy":"cpu","model":crafted,"images":["a.jpg"],
                "output_dir":dir}),
                "invalid_model",
            ),
            (
                json!({"cmd":"apply","policy":"cpu","model":not_json,"images":["a.jpg"],
                "output_dir":dir}),
                "invalid_model",
            ),
            (
                json!({"cmd":"apply","policy":"cpu","model":model,"images":[not_image],
                "output_dir":dir}),
                "image_decode_failed",
            ),
            (
                json!({"cmd":"apply","policy":"cpu","model":model,"images":[dir.join("nope.png")],
                "output_dir":dir}),
                "io_error",
            ),
            (json!({"cmd":"detect","policy":"cpu"}), "invalid_request"),
            (
                json!({"cmd":"detect","policy":"cpu","image":dir.join("nope.png")}),
                "io_error",
            ),
            (json!({"cmd":"export_cube"}), "invalid_request"),
            (
                json!({"cmd":"export_cube","model":model,"path":dir.join("x.cube"),"size":1}),
                "invalid_request",
            ),
            (
                json!({"cmd":"preview_stage","policy":"cpu","model":model,"image":not_image,
                "preview_dir":dir.join("p"),"stage":"bogus"}),
                "invalid_request",
            ),
            (json!({"cmd":"__test_panic"}), "internal_error"),
        ];
        let mut state = ServeState::default();
        for (req, expected) in cases {
            let (reply, _) = dispatch_guarded(&mut state, &req);
            assert_eq!(reply["ok"], false, "{req}: {reply}");
            assert_eq!(reply["code"], expected, "{req}: {reply}");
            assert!(
                reply["error"].as_str().is_some_and(|e| !e.is_empty()),
                "{reply}"
            );
            for row in reply["results"].as_array().into_iter().flatten() {
                assert_eq!(row["code"], expected, "{row}");
            }
        }
        let mut replies = Vec::new();
        let input = b"{not json}\n\xff\n";
        serve_lines(&mut state, std::io::Cursor::new(&input[..]), &mut |r| {
            replies.push(r.clone())
        })
        .unwrap();
        assert!(
            replies.iter().all(|r| r["code"] == "invalid_request"),
            "{replies:?}"
        );
        let (ok, _) = dispatch_guarded(&mut state, &json!({"cmd":"devices","policy":"cpu"}));
        assert!(ok.get("code").is_none(), "successful replies are unchanged");
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn unknown_cmd() {
        let mut state = ServeState::default();
        let (reply, stop) = dispatch(&mut state, &json!({"cmd":"nope"}));
        assert!(!stop);
        assert_eq!(reply["ok"], false);
    }

    #[test]
    fn invalid_image_device_is_rejected_before_processing() {
        let mut state = ServeState::default();
        for mode in [json!("cuda"), json!(false), json!(4), json!(null)] {
            let (reply, stop) = dispatch(&mut state, &json!({"cmd":"apply", "lut_device":mode}));
            assert!(!stop);
            assert_eq!(reply["ok"], false);
            assert!(reply["error"].as_str().unwrap().contains("lut_device"));
        }
    }

    #[test]
    fn removed_video_requests_fail_clearly_without_stopping_worker() {
        let mut state = ServeState::default();
        for command in ["video", "video_grade"] {
            let (reply, stop) = dispatch(&mut state, &json!({"cmd":command}));
            assert!(!stop);
            assert_eq!(reply["ok"], false);
            assert!(reply["error"]
                .as_str()
                .unwrap()
                .contains("still images only"));
        }
    }

    #[test]
    fn detect_cache_key_includes_mtime_crop_prefer() {
        let a = detect_cache_key(Path::new("x.jpg"), &DetectSettings::default());
        let b = detect_cache_key(
            Path::new("x.jpg"),
            &DetectSettings {
                crop: 0.5,
                prefer: "spyder".into(),
                ..DetectSettings::default()
            },
        );
        assert_ne!(a, b);
        for settings in [
            DetectSettings {
                conf: 0.91,
                ..DetectSettings::default()
            },
            DetectSettings {
                allow_recall: false,
                ..DetectSettings::default()
            },
            DetectSettings {
                device: "directml".into(),
                ..DetectSettings::default()
            },
        ] {
            assert_ne!(a, detect_cache_key(Path::new("x.jpg"), &settings));
        }
    }

    #[test]
    fn detection_snapshots_have_distinct_chart_identities_and_survive_selection() {
        let hs = json!({"cli_chart":"classic","patch_obbs":[[[0,0],[1,0],[1,1],[0,1]]],
            "charts":[{"cli_chart":"classic","patch_obbs":[[[0,0],[1,0],[1,1],[0,1]]]},
                {"cli_chart":"classic","patch_obbs":[[[1,0],[2,0],[2,1],[1,1]]]}]});
        let a = identify_detection_handshake(hs.clone());
        let b = identify_detection_handshake(hs);
        assert_ne!(a["snapshot_id"], b["snapshot_id"]);
        assert_ne!(a["charts"][0]["chart_id"], a["charts"][1]["chart_id"]);
        let selected = crate::run_pipeline::parse_handshake(&a, 1, false, 0.75).unwrap();
        assert_eq!(
            selected[0].chart_id,
            a["charts"][1]["chart_id"].as_str().unwrap()
        );
        assert_eq!(selected[0].snapshot_id, a["snapshot_id"].as_str().unwrap());
    }
}
