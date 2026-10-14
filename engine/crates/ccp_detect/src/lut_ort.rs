//! Variable-grid encoded-RGB LUT apply via ONNX Runtime (DirectML / CUDA / CPU).

#[cfg(feature = "ort-plane")]
mod ort_lut {
    use std::path::{Path, PathBuf};
    use std::sync::Mutex;

    use crate::paths::lut_onnx_path;
    use crate::probe::{runtime_assignment, runtime_policy, DevicePolicy};

    pub const LUT_CHUNK: usize = 262_144;

    #[derive(Clone, Copy, PartialEq, Eq)]
    enum LutEp {
        Cuda,
        DirectMl,
        OpenVino,
        CoreMl,
        Cpu,
    }

    impl LutEp {
        fn key(self) -> &'static str {
            match self {
                Self::Cuda => "cuda",
                Self::DirectMl => "directml",
                Self::OpenVino => "openvino",
                Self::CoreMl => "coreml",
                Self::Cpu => "cpu",
            }
        }
    }

    fn lut_ep_order() -> Vec<LutEp> {
        let policy = runtime_policy();
        let want = runtime_assignment().apply;
        let mut order = match policy {
            DevicePolicy::Cpu => vec![LutEp::Cpu],
            DevicePolicy::Cuda => vec![LutEp::Cuda, LutEp::DirectMl, LutEp::Cpu],
            DevicePolicy::DirectMl => vec![LutEp::DirectMl, LutEp::Cpu],
            DevicePolicy::OpenVino => vec![LutEp::OpenVino, LutEp::Cpu],
            DevicePolicy::Mlx => vec![LutEp::CoreMl, LutEp::Cpu],
            DevicePolicy::Auto => {
                let order = vec![LutEp::Cuda, LutEp::OpenVino, LutEp::DirectMl, LutEp::Cpu];
                #[cfg(target_os = "macos")]
                order.insert(order.len() - 1, LutEp::CoreMl);
                order
            }
        };
        if let Some(pref) = match want.as_str() {
            "cuda" => Some(LutEp::Cuda),
            "directml" => Some(LutEp::DirectMl),
            "openvino" => Some(LutEp::OpenVino),
            "coreml" => Some(LutEp::CoreMl),
            "cpu" => Some(LutEp::Cpu),
            _ => None,
        } {
            order.retain(|e| *e != pref);
            order.insert(0, pref);
        }
        let mut seen = Vec::new();
        order.retain(|e| {
            if seen.contains(e) {
                false
            } else {
                seen.push(*e);
                true
            }
        });
        order
    }

    fn commit_lut_session(model_path: &Path, ep: LutEp) -> Result<ort::session::Session, String> {
        use ort::ep::ExecutionProvider;
        use ort::session::Session;

        let mut builder = Session::builder().map_err(|e| e.to_string())?;
        let threads = runtime_assignment().threads.max(1) as usize;
        builder = builder
            .with_intra_threads(threads)
            .map_err(|e| e.to_string())?;
        match ep {
            LutEp::Cpu => {}
            LutEp::Cuda => {
                ort::ep::CUDA::default()
                    .register(&mut builder)
                    .map_err(|e| e.to_string())?;
            }
            LutEp::DirectMl => {
                // DirectML requires sequential execution and no memory patterns.
                builder = builder
                    .with_memory_pattern(false)
                    .map_err(|e| e.to_string())?
                    .with_parallel_execution(false)
                    .map_err(|e| e.to_string())?;
                ort::ep::DirectML::default()
                    .register(&mut builder)
                    .map_err(|e| e.to_string())?;
            }
            LutEp::OpenVino => {
                ort::ep::OpenVINO::default()
                    .register(&mut builder)
                    .map_err(|e| e.to_string())?;
            }
            LutEp::CoreMl => {
                ort::ep::CoreML::default()
                    .register(&mut builder)
                    .map_err(|e| e.to_string())?;
            }
        }
        builder
            .commit_from_file(model_path)
            .map_err(|e| e.to_string())
    }

    struct CachedLut {
        path: PathBuf,
        policy: String,
        ep: String,
        session: ort::session::Session,
    }

    static LUT_SESSION: Mutex<Option<CachedLut>> = Mutex::new(None);

    fn with_lut_session<F, T>(model_path: &Path, f: F) -> Result<(T, String), String>
    where
        F: FnOnce(&mut ort::session::Session) -> Result<T, String>,
    {
        let policy = runtime_policy().as_str().to_string();
        let mut guard = LUT_SESSION.lock().map_err(|e| e.to_string())?;
        let reuse = guard
            .as_ref()
            .is_some_and(|c| c.path == model_path && c.policy == policy);
        if !reuse {
            let mut last_err = "no LUT execution provider".to_string();
            let mut built: Option<CachedLut> = None;
            for ep in lut_ep_order() {
                match commit_lut_session(model_path, ep) {
                    Ok(session) => {
                        if ep != LutEp::Cpu {
                            eprintln!("ccp_detect: LUT ONNX on {}", ep.key());
                        } else if last_err != "no LUT execution provider" {
                            eprintln!("ccp_detect: LUT ONNX fell back to CPU ({last_err})");
                        }
                        built = Some(CachedLut {
                            path: model_path.to_path_buf(),
                            policy: policy.clone(),
                            ep: ep.key().into(),
                            session,
                        });
                        break;
                    }
                    Err(err) => {
                        if ep != LutEp::Cpu {
                            eprintln!("ccp_detect: LUT {} unavailable: {err}", ep.key());
                        }
                        last_err = err;
                    }
                }
            }
            *guard = Some(built.ok_or(last_err)?);
        }
        let cached = guard
            .as_mut()
            .ok_or_else(|| "LUT session missing".to_string())?;
        let ep = cached.ep.clone();
        let out = f(&mut cached.session)?;
        Ok((out, ep))
    }

    pub fn apply_encoded_lut_ort(
        rgb: &mut [f32],
        height: usize,
        width: usize,
        lut: &[f32],
        gs: usize,
    ) -> Result<&'static str, String> {
        use ort::value::Tensor;

        let model_path =
            lut_onnx_path().ok_or_else(|| "missing lut3d_trilinear.onnx".to_string())?;
        let gs = gs.max(2);
        let expected = gs * gs * gs * 3;
        if lut.len() != expected {
            return Err(format!("lut len {} != {expected}", lut.len()));
        }
        if rgb.len() != height * width * 3 {
            return Err("image len mismatch".into());
        }
        let pixels = height * width;
        let chunk = LUT_CHUNK;
        let mut pixel_buf = vec![0.0f32; chunk * 3];
        // Commit only after every chunk succeeds. Otherwise the caller's CPU
        // fallback could double-correct chunks already mutated before an error.
        let mut completed = Vec::with_capacity(rgb.len());
        let mut backend = String::from("cpu");
        let lut_vec = lut.to_vec();
        let mut offset = 0usize;
        while offset < pixels {
            let n = (pixels - offset).min(chunk);
            for i in 0..n {
                let src = (offset + i) * 3;
                let dst = i * 3;
                pixel_buf[dst] = rgb[src];
                pixel_buf[dst + 1] = rgb[src + 1];
                pixel_buf[dst + 2] = rgb[src + 2];
            }
            if n < chunk {
                for i in n..chunk {
                    let dst = i * 3;
                    pixel_buf[dst] = 0.0;
                    pixel_buf[dst + 1] = 0.0;
                    pixel_buf[dst + 2] = 0.0;
                }
            }
            let pixels_in = Tensor::from_array(([chunk as i64, 3i64], pixel_buf.clone()))
                .map_err(|e| e.to_string())?;
            let lut_in =
                Tensor::from_array(([gs as i64, gs as i64, gs as i64, 3i64], lut_vec.clone()))
                    .map_err(|e| e.to_string())?;
            let (chunk_out, ep) = with_lut_session(&model_path, |session| {
                let outputs = session
                    .run(ort::inputs!["pixels" => pixels_in, "lut" => lut_in])
                    .map_err(|e| e.to_string())?;
                let (_name, value) = outputs
                    .iter()
                    .next()
                    .ok_or_else(|| "empty LUT output".to_string())?;
                let (_shape, data) = value
                    .try_extract_tensor::<f32>()
                    .map_err(|e| e.to_string())?;
                Ok(data[..n * 3].to_vec())
            })?;
            backend = ep;
            completed.extend_from_slice(&chunk_out[..n * 3]);
            offset += n;
        }
        rgb.copy_from_slice(&completed);
        Ok(match backend.as_str() {
            "directml" => "directml",
            "cuda" => "cuda",
            "openvino" => "openvino",
            "coreml" => "coreml",
            _ => "cpu",
        })
    }
}

#[cfg(feature = "ort-plane")]
pub use ort_lut::{apply_encoded_lut_ort, LUT_CHUNK};

#[cfg(not(feature = "ort-plane"))]
pub const LUT_CHUNK: usize = 262_144;

#[cfg(not(feature = "ort-plane"))]
pub fn apply_encoded_lut_ort(
    _rgb: &mut [f32],
    _height: usize,
    _width: usize,
    _lut: &[f32],
    _gs: usize,
) -> Result<&'static str, String> {
    Err("ort not linked".into())
}
