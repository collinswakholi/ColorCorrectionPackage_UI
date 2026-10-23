//! GPU-only fallback for WSL's Vulkan/D3D12 translation driver. It does not
//! emulate missing graphics features or register a CPU tensor backend.
use burn::tensor::wgpu::{wgpu, WgpuSetup};
use burn::tensor::{Device, Tensor, TensorData};
use sha2::{Digest, Sha256};
use std::{
    path::Path,
    sync::{Mutex, OnceLock},
};

#[allow(dead_code)]
mod plane {
    include!(concat!(
        env!("OUT_DIR"),
        "/models/plane_det_model_YOLO_512_n.rs"
    ));
}
#[allow(dead_code)]
mod chart {
    include!(concat!(
        env!("OUT_DIR"),
        "/models/best_assemble_fp32_1280.rs"
    ));
}
#[allow(dead_code)]
mod large {
    include!(concat!(
        env!("OUT_DIR"),
        "/models/best_assemble_fp32_1536.rs"
    ));
}

fn guarded<T>(f: impl FnOnce() -> Result<T, String>) -> Result<T, String> {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)).unwrap_or_else(|error| {
        let message = error
            .downcast_ref::<String>()
            .map(String::as_str)
            .or_else(|| error.downcast_ref::<&str>().copied())
            .unwrap_or("GPU runtime failure");
        Err(format!("Rust WebGPU inference failed: {message}"))
    })
}

fn block_on<F: std::future::Future>(f: F) -> F::Output {
    use std::{
        pin::pin,
        sync::Arc,
        task::{Context, Poll, Wake, Waker},
    };
    struct ThreadWake(std::thread::Thread);
    impl Wake for ThreadWake {
        fn wake(self: Arc<Self>) {
            self.0.unpark();
        }
    }
    let waker = Waker::from(Arc::new(ThreadWake(std::thread::current())));
    let mut cx = Context::from_waker(&waker);
    let mut f = pin!(f);
    loop {
        match f.as_mut().poll(&mut cx) {
            Poll::Ready(v) => return v,
            Poll::Pending => std::thread::park(),
        }
    }
}

#[derive(Clone)]
struct Runtime {
    device: Device,
    name: String,
    selector: String,
}
static RUNTIME: OnceLock<Mutex<Option<Runtime>>> = OnceLock::new();
fn runtime() -> Result<Runtime, String> {
    let selector = std::env::var("CCP_WGPU_INFERENCE_ADAPTER")
        .unwrap_or_default()
        .to_lowercase();
    let mut cache = RUNTIME
        .get_or_init(|| Mutex::new(None))
        .lock()
        .map_err(|e| e.to_string())?;
    if let Some(runtime) = cache.as_ref() {
        if runtime.selector != selector {
            return Err("Inference adapter changed; restart the worker.".into());
        }
        return Ok(runtime.clone());
    }
    let mut descriptor = wgpu::InstanceDescriptor::new_without_display_handle();
    descriptor.backends = wgpu::Backends::VULKAN;
    // Non-conformant translation drivers require an explicit environment opt-in.
    // This only permits enumeration; capabilities are never altered.
    descriptor.flags = descriptor.flags.with_env();
    let instance = wgpu::Instance::new(descriptor);
    let mut adapters = block_on(instance.enumerate_adapters(wgpu::Backends::VULKAN));
    adapters.sort_by_key(|a| a.get_info().device_type != wgpu::DeviceType::DiscreteGpu);
    let adapter = adapters
        .into_iter()
        .find(|a| {
            let info = a.get_info();
            matches!(
                info.device_type,
                wgpu::DeviceType::DiscreteGpu | wgpu::DeviceType::IntegratedGpu
            ) && (selector.is_empty() || info.name.to_lowercase().contains(&selector))
        })
        .ok_or(
            "No compatible hardware Vulkan adapter for inference; software adapters are disabled.",
        )?;
    let info = adapter.get_info();
    // CubeCL's WGSL compiler uses advertised features for kernel selection.
    // Enable exactly those features, except directly mapped primary buffers.
    let (device, queue) = block_on(
        adapter.request_device(&wgpu::DeviceDescriptor {
            label: Some("ColorCorrect inference"),
            required_features: adapter
                .features()
                .difference(wgpu::Features::MAPPABLE_PRIMARY_BUFFERS),
            required_limits: adapter.limits(),
            memory_hints: wgpu::MemoryHints::MemoryUsage,
            trace: wgpu::Trace::Off,
            experimental_features: unsafe { wgpu::ExperimentalFeatures::enabled() },
        }),
    )
    .map_err(|e| e.to_string())?;
    let device = Device::wgpu_options()
        .setup(WgpuSetup {
            instance,
            adapter,
            device,
            queue,
            backend: wgpu::Backend::Vulkan,
        })
        .init()
        .map_err(|e| e.to_string())?;
    let runtime = Runtime {
        device,
        name: format!("webgpu:wgpu:vulkan:{}", info.name),
        selector,
    };
    *cache = Some(runtime.clone());
    Ok(runtime)
}

fn model_index(bytes: &[u8]) -> Result<usize, String> {
    let hash = format!("{:x}", Sha256::digest(bytes));
    [env!("CCP_MODEL_HASH_0"),env!("CCP_MODEL_HASH_1"),env!("CCP_MODEL_HASH_2")]
        .iter().position(|expected|*expected==hash)
        .ok_or_else(||"This ONNX model does not match a bundled Rust WebGPU model. Use the ONNX provider or rebuild the package with the replacement model.".into())
}

enum Model {
    Plane(Box<plane::Model>),
    Chart(Box<chart::Model>),
    Large(Box<large::Model>),
}
pub struct Session {
    runtime: Runtime,
    model: Model,
    size: usize,
}
impl Session {
    pub fn new(path: &Path) -> Result<Self, String> {
        guarded(|| {
            let bytes = std::fs::read(path).map_err(|e| e.to_string())?;
            let index = model_index(&bytes)?; // Check identity before allocating a GPU.
            let runtime = runtime()?;
            let model = match index {
                0 => Model::Plane(Box::new(plane::Model::from_embedded(&runtime.device))),
                1 => Model::Chart(Box::new(chart::Model::from_embedded(&runtime.device))),
                _ => Model::Large(Box::new(large::Model::from_embedded(&runtime.device))),
            };
            Ok(Self {
                runtime,
                model,
                size: [512, 1280, 1536][index],
            })
        })
    }
    pub fn backend(&self) -> &str {
        &self.runtime.name
    }
    pub fn forward(&self, input: Vec<f32>, size: usize) -> Result<(Vec<i64>, Vec<f32>), String> {
        guarded(|| {
            if size != self.size
                || input.len() != 3 * size * size
                || !input.iter().all(|v| v.is_finite())
            {
                return Err("Invalid inference input dimensions or values".into());
            }
            let input = Tensor::<4>::from_data(
                TensorData::new(input, [1, 3, size, size]),
                &self.runtime.device,
            );
            let output = match &self.model {
                Model::Plane(m) => m.forward(input),
                Model::Chart(m) => m.forward(input),
                Model::Large(m) => m.forward(input),
            };
            let data = output.into_data();
            let dims = data.shape().iter().map(|&v| v as i64).collect();
            let values = data.try_to_vec::<f32>().map_err(|e| format!("{e:?}"))?;
            if !values.iter().all(|v| v.is_finite()) {
                return Err("Nonfinite GPU inference output".into());
            }
            Ok((dims, values))
        })
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn rejects_unknown_or_modified_models_without_gpu_initialization() {
        assert!(super::model_index(b"not the bundled model").is_err());
    }
}
