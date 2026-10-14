//! Opt2 inference: LibTorch TorchScript when CUDA/MPS is assigned; ONNX Runtime otherwise.

use std::path::Path;

use crate::probe::runtime_assignment;
use crate::{DetectResult, DetectSettings};

pub const LIBTORCH_DISABLED: &str =
    "ccp_detect was built without LibTorch. Enable the `tch` Cargo feature so detect uses the Opt2 TorchScript graphs.";

/// Clear error used when the `tch` feature is off.
pub fn libtorch_required_error() -> DetectResult {
    DetectResult {
        error: LIBTORCH_DISABLED.to_string(),
        backend: "torchscript".into(),
        runtime: "libtorch".into(),
        recall_source: "none".into(),
        ..DetectResult::default()
    }
}

fn should_use_ort_detect() -> bool {
    match runtime_assignment().detect.as_str() {
        "directml" | "openvino" | "coreml" => true,
        "cuda" | "mps" => false,
        "cpu" | "" => cfg!(feature = "ort-plane"),
        _ => cfg!(feature = "ort-plane"),
    }
}

pub fn warmup_detector(settings: &DetectSettings) -> DetectResult {
    if should_use_ort_detect() {
        #[cfg(feature = "ort-plane")]
        {
            return crate::infer_ort::warmup_detector_ort(settings);
        }
        #[cfg(not(feature = "ort-plane"))]
        {
            return DetectResult {
                error: "ccp_detect was built without ONNX Runtime. Enable the `ort-plane` feature."
                    .into(),
                backend: "onnx".into(),
                runtime: "ort".into(),
                ..DetectResult::default()
            };
        }
    }
    #[cfg(feature = "tch")]
    {
        torch_engine::warmup_detector(settings)
    }
    #[cfg(not(feature = "tch"))]
    {
        libtorch_required_error()
    }
}

pub fn detect_chart(image_path: &Path, settings: &DetectSettings) -> DetectResult {
    if should_use_ort_detect() {
        #[cfg(feature = "ort-plane")]
        {
            return crate::infer_ort::detect_chart_ort(image_path, settings);
        }
        #[cfg(not(feature = "ort-plane"))]
        {
            return DetectResult {
                error: "ccp_detect was built without ONNX Runtime. Enable the `ort-plane` feature."
                    .into(),
                backend: "onnx".into(),
                runtime: "ort".into(),
                ..DetectResult::default()
            };
        }
    }
    #[cfg(feature = "tch")]
    {
        torch_engine::detect_chart(image_path, settings)
    }
    #[cfg(not(feature = "tch"))]
    {
        libtorch_required_error()
    }
}

#[cfg(feature = "tch")]
mod torch_engine {
    use super::*;
    use std::path::PathBuf;
    use std::sync::Mutex;

    use tch::{CModule, Device, IValue, Kind, Tensor};

    use crate::assemble::assemble_charts;
    use crate::decode::{decode_obb, dets_from_raw, letterbox_rgb, preds_to_obb_rows};
    use crate::geom::{
        cli_chart_from_detector, format_quad, DECODE_IOU, DEFAULT_CONF, DEFAULT_CROP,
        DEFAULT_MAX_DET, PRIMARY_SIZE, RECALL_SIZE,
    };
    use crate::handshake::{make_handshake, warmup_json};
    use crate::paths::{default_deploy_dir, primary_graph_path, recall_graph_path};

    struct TorchEngine {
        primary: CModule,
        recall: Option<CModule>,
        device: Device,
        half: bool,
        deploy_dir: PathBuf,
        want: String,
    }

    static ENGINE: Mutex<Option<TorchEngine>> = Mutex::new(None);

    fn accel_flag(device_flag: &str) -> String {
        let flag = if device_flag.is_empty() {
            "gpu"
        } else {
            device_flag
        };
        flag.trim().to_ascii_lowercase()
    }

    fn device_label(device: Device) -> &'static str {
        match device {
            Device::Cuda(_) => "cuda",
            Device::Mps => "mps",
            _ => "cpu",
        }
    }

    fn is_oom(err: &str) -> bool {
        let e = err.to_ascii_lowercase();
        e.contains("out of memory") || e.contains("oom") || e.contains("cuda oom")
    }

    fn load_module(path: &Path, device: Device, half: bool) -> Result<CModule, String> {
        let mut module = CModule::load_on_device(path, device)
            .map_err(|e| format!("load {}: {e}", path.display()))?;
        module.set_eval();
        let kind = if half { Kind::Half } else { Kind::Float };
        module.to(device, kind, false);
        Ok(module)
    }

    fn try_new_engine(deploy_dir: &Path, want: &str) -> Result<TorchEngine, String> {
        let primary_path = primary_graph_path(deploy_dir);
        if !primary_path.is_file() {
            return Err(format!("missing {}", primary_path.display()));
        }
        let try_cuda = want == "cuda" || want == "gpu";
        let try_mps =
            (want == "mps" || want == "mlx" || want == "gpu") && cfg!(target_os = "macos");
        if try_cuda {
            match load_module(&primary_path, Device::Cuda(0), true) {
                Ok(primary) => {
                    return Ok(TorchEngine {
                        primary,
                        recall: None,
                        device: Device::Cuda(0),
                        half: true,
                        deploy_dir: deploy_dir.to_path_buf(),
                        want: want.to_string(),
                    });
                }
                Err(err) => {
                    eprintln!("ccp_detect: CUDA load failed: {err}");
                }
            }
        }
        if try_mps {
            match load_module(&primary_path, Device::Mps, false) {
                Ok(primary) => {
                    return Ok(TorchEngine {
                        primary,
                        recall: None,
                        device: Device::Mps,
                        half: false,
                        deploy_dir: deploy_dir.to_path_buf(),
                        want: want.to_string(),
                    });
                }
                Err(err) => {
                    eprintln!("ccp_detect: MPS load failed: {err}");
                }
            }
        }
        let primary = load_module(&primary_path, Device::Cpu, false)?;
        Ok(TorchEngine {
            primary,
            recall: None,
            device: Device::Cpu,
            half: false,
            deploy_dir: deploy_dir.to_path_buf(),
            want: want.to_string(),
        })
    }

    fn engine(deploy_dir: &Path, want: &str) -> Result<(), String> {
        let mut guard = ENGINE.lock().map_err(|e| e.to_string())?;
        if let Some(eng) = guard.as_ref() {
            if eng.deploy_dir == deploy_dir && eng.want == want {
                return Ok(());
            }
        }
        match try_new_engine(deploy_dir, want) {
            Ok(eng) => {
                *guard = Some(eng);
                Ok(())
            }
            Err(err) if want != "cpu" && is_oom(&err) => {
                let eng = try_new_engine(deploy_dir, "cpu")
                    .map_err(|e| format!("failed: oom_on_warmup ({err}); cpu fallback: {e}"))?;
                *guard = Some(eng);
                Ok(())
            }
            Err(err) => Err(err),
        }
    }

    fn with_engine<T, F>(deploy_dir: &Path, want: &str, f: F) -> Result<T, String>
    where
        F: FnOnce(&mut TorchEngine) -> Result<T, String>,
    {
        engine(deploy_dir, want)?;
        let mut guard = ENGINE.lock().map_err(|e| e.to_string())?;
        let eng = guard
            .as_mut()
            .ok_or_else(|| "engine not loaded".to_string())?;
        f(eng)
    }

    fn ensure_recall(eng: &mut TorchEngine) -> Result<(), String> {
        if eng.recall.is_some() {
            return Ok(());
        }
        let path = recall_graph_path(&eng.deploy_dir);
        if !path.is_file() {
            return Err(format!("missing {}", path.display()));
        }
        let module = load_module(&path, eng.device, eng.half)?;
        eng.recall = Some(module);
        Ok(())
    }

    fn warmup_module(
        module: &CModule,
        imgsz: i32,
        device: Device,
        half: bool,
    ) -> Result<(), String> {
        let kind = if half { Kind::Half } else { Kind::Float };
        let input = Tensor::zeros([1, 3, i64::from(imgsz), i64::from(imgsz)], (kind, device));
        let _guard = tch::no_grad_guard();
        let _ = module
            .forward_is(&[IValue::from(input)])
            .map_err(|e| e.to_string())?;
        if device.is_cuda() {
            tch::Cuda::synchronize(cuda_index(device));
        }
        Ok(())
    }

    fn cuda_index(device: Device) -> i64 {
        match device {
            Device::Cuda(i) => i as i64,
            _ => 0,
        }
    }

    fn make_input(canvas: &[u8], imgsz: i32, device: Device, half: bool) -> Tensor {
        let floats: Vec<f32> = canvas.iter().map(|&b| f32::from(b) / 255.0).collect();
        let kind = if half { Kind::Half } else { Kind::Float };
        Tensor::from_slice(&floats)
            .reshape([i64::from(imgsz), i64::from(imgsz), 3])
            .permute([2, 0, 1])
            .unsqueeze(0)
            .to_kind(kind)
            .to_device(device)
            .contiguous()
    }

    fn tensor_from_ivalue(out: IValue) -> Result<Tensor, String> {
        match out {
            IValue::Tensor(t) => Ok(t),
            IValue::Tuple(mut xs) if !xs.is_empty() => match xs.remove(0) {
                IValue::Tensor(t) => Ok(t),
                other => tensor_from_ivalue(other),
            },
            IValue::GenericList(mut xs) if !xs.is_empty() => tensor_from_ivalue(xs.remove(0)),
            IValue::TensorList(mut xs) if !xs.is_empty() => Ok(xs.remove(0)),
            _ => Err("unexpected TorchScript output".into()),
        }
    }

    #[allow(clippy::too_many_arguments)] // Keeps inference knobs explicit at the backend boundary.
    fn predict_module(
        module: &CModule,
        rgb: &[f32],
        width: i32,
        height: i32,
        imgsz: i32,
        conf: f32,
        iou: f32,
        max_det: i32,
        device: Device,
        half: bool,
    ) -> Result<Vec<crate::geom::Det>, String> {
        let lb = letterbox_rgb(rgb, width, height, imgsz);
        let _guard = tch::no_grad_guard();
        let input = make_input(&lb.canvas, imgsz, device, half);
        let out = module
            .forward_is(&[IValue::from(input)])
            .map_err(|e| e.to_string())?;
        let preds = tensor_from_ivalue(out)?;
        if device.is_cuda() {
            tch::Cuda::synchronize(cuda_index(device));
        }
        let cpu = preds
            .to_device(Device::Cpu)
            .to_kind(Kind::Float)
            .contiguous();
        let dims: Vec<usize> = cpu.size().into_iter().map(|d| d as usize).collect();
        let flat: Vec<f32> = Vec::<f32>::try_from(cpu.reshape([-1])).map_err(|e| e.to_string())?;
        let rows = preds_to_obb_rows(&flat, &dims);
        let raw = decode_obb(&rows, conf, iou, max_det.max(1) as usize);
        Ok(dets_from_raw(&raw, imgsz, lb.gain, lb.pad_x, lb.pad_y))
    }

    fn load_rgb_f32(path: &Path) -> Result<(Vec<f32>, i32, i32), String> {
        let img = ccp_core::sample::load_image(path).map_err(|e| e.to_string())?;
        Ok((img.rgb, img.width as i32, img.height as i32))
    }

    fn resolve_deploy(settings: &DetectSettings) -> PathBuf {
        if settings.deploy_dir.as_os_str().is_empty() {
            default_deploy_dir()
        } else {
            settings.deploy_dir.clone()
        }
    }

    pub fn warmup_detector(settings: &DetectSettings) -> DetectResult {
        let mut result = DetectResult {
            backend: "torchscript".into(),
            runtime: "libtorch".into(),
            recall_source: "none".into(),
            ..DetectResult::default()
        };
        let deploy = resolve_deploy(settings);
        let want = accel_flag(&settings.device);
        match with_engine(&deploy, &want, |eng| {
            warmup_module(&eng.primary, PRIMARY_SIZE, eng.device, eng.half)?;
            Ok(device_label(eng.device).to_string())
        }) {
            Ok(device) => {
                result.ok = true;
                result.device = device;
                result.json = warmup_json(&result.device);
                result
            }
            Err(err) => {
                result.error = if is_oom(&err) {
                    format!("failed: oom_on_warmup ({err})")
                } else {
                    err
                };
                result
            }
        }
    }

    pub fn detect_chart(image_path: &Path, settings: &DetectSettings) -> DetectResult {
        let mut result = DetectResult {
            backend: "torchscript".into(),
            runtime: "libtorch".into(),
            recall_source: "none".into(),
            ..DetectResult::default()
        };
        let (rgb, width, height) = match load_rgb_f32(image_path) {
            Ok(v) => v,
            Err(err) => {
                result.error = err;
                return result;
            }
        };
        let deploy = resolve_deploy(settings);
        let want = accel_flag(&settings.device);
        let conf = if settings.conf > 0.0 {
            settings.conf
        } else {
            DEFAULT_CONF
        };
        let iou = if settings.iou > 0.0 {
            settings.iou
        } else {
            DECODE_IOU
        };
        let max_det = if settings.max_det > 0 {
            settings.max_det
        } else {
            DEFAULT_MAX_DET
        };
        let crop = if settings.crop > 0.0 {
            settings.crop
        } else {
            DEFAULT_CROP
        };

        let run = with_engine(&deploy, &want, |eng| {
            result.device = device_label(eng.device).into();
            let mut dets = predict_module(
                &eng.primary,
                &rgb,
                width,
                height,
                PRIMARY_SIZE,
                conf,
                iou,
                max_det,
                eng.device,
                eng.half,
            )?;
            result.recall_source = "primary".into();
            let mut charts = assemble_charts(&dets, &settings.prefer, crop, width, height);
            if charts.is_empty() && settings.allow_recall {
                ensure_recall(eng)?;
                let recall = eng
                    .recall
                    .as_ref()
                    .ok_or_else(|| "recall module missing".to_string())?;
                dets = predict_module(
                    recall,
                    &rgb,
                    width,
                    height,
                    RECALL_SIZE,
                    conf.min(0.05),
                    iou,
                    max_det,
                    eng.device,
                    eng.half,
                )?;
                result.recall_source = "recall_1536".into();
                charts = assemble_charts(&dets, &settings.prefer, crop, width, height);
            }
            result.n_dets = dets.len() as i32;
            Ok(charts)
        });

        match run {
            Ok(charts) => {
                if charts.is_empty() {
                    result.error = "no lattice".into();
                    result.n_charts = 0;
                    return result;
                }
                result.ok = true;
                result.n_charts = charts.len() as i32;
                result.detection = charts[0].clone();
                result.chart_type = charts[0].chart_type.clone();
                result.cli_chart = cli_chart_from_detector(&charts[0].chart_type).to_string();
                result.quad = format_quad(charts[0].corners);
                result.charts = charts.clone();
                match make_handshake(&result, &charts, crop, image_path) {
                    Ok(json) => result.json = json,
                    Err(err) => result.error = err.to_string(),
                }
                result
            }
            Err(err) => {
                result.error = err;
                result
            }
        }
    }
}
