//! First-class NDJSON serve (stdin/stdout). Host drains stderr.

use std::collections::{HashMap, HashSet};
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

use crate::run_pipeline::{
    apply_saved_to_image_ex_with_progress, export_cube_from_saved, json_bool, json_f64, json_i64, json_str,
    last_apply_backend, load_saved, run_from_json_ex, ApplyOptions, FfcGainCache, PreviewMode,
    SavedPipeline,
};

#[derive(Debug)]
pub struct ServeState {
    pub policy: String,
    pub device: String,
    pub warmed: bool,
    pub last: Option<SavedPipeline>,
    pub detect_cache: HashMap<String, PathBuf>,
    pub ffc_cache: Arc<FfcGainCache>,
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
            obj.insert("inference".into(),json!({"plane":ccp_detect::plane::last_plane_inference()}));
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
    settings.device = policy.as_str().into();
    settings.prefer = json_str(req, &["prefer"]).unwrap_or("").to_string();
    settings.conf = json_f64(req, &["conf"], f64::from(DEFAULT_CONF)) as f32;
    settings.crop = json_f64(req, &["crop"], DEFAULT_CROP);
    settings.allow_recall = json_bool(req, &["allow_recall"], true);
    settings
}

fn detect_cache_key(path: &Path, settings: &DetectSettings) -> String {
    use ccp_detect::cache_key::{model_cache_identity, runtime_cache_identity};
    use ccp_detect::paths::{
        primary_onnx_path, recall_onnx_path,
    };
    let canonical = path.canonicalize().unwrap_or_else(|_| path.to_path_buf());
    let metadata = std::fs::metadata(path).ok();
    let deploy = if settings.deploy_dir.as_os_str().is_empty() {
        ccp_detect::default_deploy_dir()
    } else {
        settings.deploy_dir.clone()
    };
    let models = [
        primary_onnx_path(&deploy),
        recall_onnx_path(&deploy),
    ]
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
    #[cfg(feature="ort-plane")]
    if warmed {
        if let Some(actual)=ccp_detect::last_detect_ep() {return format!("Warmed · {actual} inference");}
    }
    let report = probe_accelerators(policy);
    let warm = if warmed { "Warmed" } else { "Ready" };
    format!("{warm} · {}", report.recommended)
}

fn emit_ready(state: &ServeState) {
    let ready = json!({
        "ok": true,
        "event": "ready",
        "device": state.device,
        "warmed": state.warmed,
        "title": ready_title(state.warmed, &state.policy),
        "protocol_version": 2,
        "capabilities": ["custom_references_v1", "chart_identity_v1", "combined_lut65_v1", "cpu_inference_v1"],
        "portable_lut": cfg!(feature = "wgpu-lut"),
    });
    emit_json(&ready);
}

fn cmd_devices(state: &ServeState, req: &Value) -> Value {
    let policy = policy_from_req(req, &state.policy);
    let report = probe_accelerators(&policy);
    set_runtime_from_report(&report);
    let mut v = serde_json::to_value(&report).unwrap_or_else(|_| json!({}));
    if let Some(obj) = v.as_object_mut() {
        obj.insert("ok".into(), json!(true));
        #[cfg(feature="ort-plane")]
        if state.warmed {
            let inference=ccp_detect::infer_ort::inference_records(false);
            if inference.pointer("/detect/requested_policy").and_then(Value::as_str)==Some(policy.as_str()) {
                obj.insert("verified_inference".into(),inference);
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
            "warmed": false,
            "device": state.device,
            "error": result.error,
        })
    }
}

fn cmd_setup_probe(state: &mut ServeState, req: &Value) -> Value {
    let inference=cmd_warmup(state,req);
    let mut lut=Vec::new();
    for b in 0..2 {for g in 0..2 {for r in 0..2 {lut.extend_from_slice(&[r as f32,g as f32,b as f32]);}}}
    let mut pixels=vec![0.25,0.5,0.75];
    let cpu=json_bool(req,&["force_cpu"],false);
    let correction=if cpu {
        ccp_detect::lut_chunks::apply_chunks(&mut pixels,&lut,2,1,|_|Err("CPU requested".into()))
            .map(|_|json!({"status":"verified","backend":"cpu","fallback":null}))
    } else {
        ccp_detect::lut_wgpu::apply(&mut pixels,&lut,2)
            .map(|actual|json!({"status":"verified","backend":actual.backend,"fallback":actual.chunks.fallback_reason}))
    };
    let correction=correction.unwrap_or_else(|reason|json!({"status":"failed","error":reason}));
    json!({"ok":inference["ok"]==true && correction["status"]=="verified",
        "setup_verified":inference["ok"]==true && correction["status"]=="verified",
        "inference":inference,"correction":correction})
}

fn cmd_detect(state: &mut ServeState, req: &Value) -> Value {
    let image = match json_str(req, &["image", "input", "image_path"]) {
        Some(p) => PathBuf::from(p),
        None => return json!({"ok":false,"error":"detect requires image"}),
    };
    if !image.is_file() {
        return json!({"ok":false,"error": format!("image not found: {}", image.display())});
    }
    // Fresh SDK clients need no preceding devices/warmup request. Routing in
    // ccp_detect reads the runtime assignment, so synchronize it before both
    // cache identity construction and inference, just as run/apply already do.
    state.policy = policy_from_req(req, &state.policy);
    sync_runtime(state, req);
    let settings = detect_settings(state, req);
    if json_bool(req, &["retry_inference"], false) {
        #[cfg(feature="ort-plane")]
        ccp_detect::reset_inference_sessions();
        state.detect_cache.clear();
    }
    let key = detect_cache_key(&image, &settings);
    let bypass_cache = json_bool(req, &["bypass_cache"], false);
    if let Some(cached) = state.detect_cache.get(&key).filter(|_| !bypass_cache) {
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
                            if let Some(parent) = returned.parent() {
                                let _ = std::fs::create_dir_all(parent);
                            }
                            if let Err(error) = std::fs::write(returned, &text) {
                                return json!({"ok":false,"error":error.to_string()});
                            }
                        }
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
            "error": "detect produced an empty handshake",
            "n_charts": result.n_charts,
            "chart_type": result.chart_type,
        });
    }
    let handshake = match serde_json::from_str::<Value>(&result.json) {
        Ok(value) => identify_detection_handshake(value),
        Err(error) => {
            return json!({"ok":false,"error":format!("invalid detection handshake: {error}")})
        }
    };
    let handshake_out = json_str(req, &["handshake_out", "json_out"]).map(PathBuf::from);
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
    if let Some(parent) = out.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Err(e) = std::fs::write(&out, serde_json::to_vec(&handshake).unwrap_or_default()) {
        return json!({"ok":false,"error": e.to_string()});
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
            state.detect_cache.insert(key, cache_path);
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
    emit_progress(&format!("Applying model to {} images", images.len()));

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
    let apply_one = |img: &str, out: &Path, preview: Option<&Path>| -> Value {
        emit_progress(&format!(
            "Apply · {}",
            Path::new(img)
                .file_name()
                .and_then(|s| s.to_str())
                .unwrap_or(img)
        ));
        match apply_saved_to_image_ex_with_progress(&saved, Path::new(img), out, preview, &apply, &mut emit_progress) {
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
        other => return json!({"ok":false,"error": format!("unknown stage: {other}")}),
    }
    match apply_saved_to_image_ex_with_progress(&saved, &image, &dummy, Some(&preview_dir), &apply, &mut emit_progress) {
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

pub fn dispatch(state: &mut ServeState, req: &Value) -> (Value, bool) {
    // Reset per request so a preview or previous batch cannot leak its choice.
    crate::run_pipeline::set_auto_lut_request(false);
    crate::run_pipeline::set_cpu_lut_request(json_bool(req, &["cpu_lut"], false));
    if let Some(mode) = req.get("lut_device") {
        match mode.as_str() {
            Some("auto") => crate::run_pipeline::set_auto_lut_request(true),
            Some("cpu") => crate::run_pipeline::set_cpu_lut_request(true),
            Some("gpu") => {},
            _ => return (json!({"ok":false,"error":"lut_device must be auto, cpu, or gpu"}), false),
        }
    }
    let cmd = json_str(req, &["cmd", "command"])
        .unwrap_or("")
        .to_ascii_lowercase();
    if req.get("cc_method").is_some() {
        return (json!({"ok":false,"error":"cc_method was removed; use fast_mode: true for the optional combined LUT"}), false);
    }
    if let Some(value) = req.get("fast_mode") {
        if !value.is_boolean() || !matches!(cmd.as_str(), "run" | "apply" | "preview_stage") {
            return (json!({"ok":false,"error":"fast_mode must be a boolean on run/apply/preview_stage requests"}), false);
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
            json!({"ok":false,"error":"Video processing has been removed; this app supports still images only."}),
            false,
        ),
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
        assert_eq!(ccp_detect::runtime_assignment().detect, auto.assignment.detect);
        assert_eq!(ccp_detect::runtime_assignment().plane, auto.assignment.plane);
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
        let mut settings = DetectSettings::default();
        settings.conf = 0.91;
        assert_ne!(a, detect_cache_key(Path::new("x.jpg"), &settings));
        settings = DetectSettings::default();
        settings.allow_recall = false;
        assert_ne!(a, detect_cache_key(Path::new("x.jpg"), &settings));
        settings = DetectSettings::default();
        settings.device = "directml".into();
        assert_ne!(a, detect_cache_key(Path::new("x.jpg"), &settings));
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
