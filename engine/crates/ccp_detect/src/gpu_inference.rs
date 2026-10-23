//! One GPU-only session contract for chart and plane networks.
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use ort::{environment::Environment, ep::ExecutionProvider, memory::DeviceType, session::Session};
use crate::probe::{runtime_assignment, runtime_policy, DevicePolicy};

struct RegisteredLibrary {
    path: PathBuf,
    library: ort::ep::ExecutionProviderLibrary,
    backend: Option<&'static str>,
}
static REGISTERED: OnceLock<Mutex<Option<RegisteredLibrary>>> = OnceLock::new();

/// Called after all model sessions have been dropped, before process teardown.
/// Native WebGPU owns driver threads and must not be torn down under the Windows
/// loader lock during the Rust binding's process-exit environment callback.
pub fn shutdown() -> Result<(), String> {
    if let Some(lock) = REGISTERED.get() {
        if let Some(registered) = lock.lock().map_err(|e| e.to_string())?.take() {
            registered.library.unregister().map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum GpuEp { DirectMl, WebGpu, Vulkan }
impl GpuEp {
    pub fn key(self) -> &'static str { match self { Self::DirectMl => "directml", Self::WebGpu => "webgpu", Self::Vulkan => "vulkan" } }
}

fn dawn_backend(ep: GpuEp) -> &'static str {
    if ep == GpuEp::Vulkan || cfg!(target_os = "linux") { "Vulkan" }
    else if cfg!(target_os = "macos") { "Metal" }
    else { "D3D12" }
}

fn claim_backend(current: &mut Option<&'static str>, requested: &'static str) -> Result<(), String> {
    if current.is_some_and(|backend| backend != requested) {
        return Err("WebGPU graphics backend changed; restart the worker to apply this engine change.".into());
    }
    // Even failed initialization may leave a native context alive. Never reuse
    // it for another backend, or unload it while cached sessions still exist.
    *current = Some(requested);
    Ok(())
}

pub fn provider_order() -> Vec<GpuEp> {
    match runtime_policy() {
        DevicePolicy::DirectMl => vec![GpuEp::DirectMl],
        DevicePolicy::WebGpu => vec![GpuEp::WebGpu],
        DevicePolicy::Vulkan => vec![GpuEp::Vulkan],
        _ if cfg!(windows) => vec![GpuEp::DirectMl, GpuEp::WebGpu],
        _ => vec![GpuEp::WebGpu],
    }
}

/// Shared model boundary: both runtimes return owned tensors to the same
/// validation/geometry code, so selection cannot change chart interpretation.
pub enum InferenceSession {
    Ort(Session),
    #[cfg(all(target_os="linux",feature="rust-inference"))]
    Rust(ccp_wgpu_infer::Session),
}
impl InferenceSession {
    pub fn backend(&self, requested: GpuEp) -> String {
        match self {
            Self::Ort(_) => requested.key().into(),
            #[cfg(all(target_os="linux",feature="rust-inference"))]
            Self::Rust(session) => session.backend().into(),
        }
    }
    pub fn forward(&mut self, input: Vec<f32>, size: usize) -> Result<(Vec<i64>,Vec<f32>),String> {
        match self {
            Self::Ort(session) => {
                let input=ort::value::Tensor::from_array(([1usize,3,size,size],input)).map_err(|e|e.to_string())?;
                let outputs=session.run(ort::inputs![input]).map_err(|e|e.to_string())?;
                let (_,value)=outputs.iter().next().ok_or("Empty inference output")?;
                let (shape,values)=value.try_extract_tensor::<f32>().map_err(|e|e.to_string())?;
                Ok((shape.iter().copied().collect(),values.to_vec()))
            },
            #[cfg(all(target_os="linux",feature="rust-inference"))]
            Self::Rust(session) => session.forward(input,size),
        }
    }
}

pub fn commit_inference_session(path: &Path, ep: GpuEp) -> Result<InferenceSession,String> {
    #[cfg(all(target_os="linux",feature="rust-inference"))]
    if ep != GpuEp::DirectMl && Path::new("/dev/dxg").exists() {
        // WSL's Dozen lacks fullDrawIndexUint32. Dawn requires this drawing
        // feature even for ONNX compute; Rust/wgpu needs only actual compute
        // capabilities. Native Linux keeps the established ONNX provider.
        return ccp_wgpu_infer::Session::new(path).map(InferenceSession::Rust);
    }
    commit_session(path,ep).map(InferenceSession::Ort)
}

fn plugin_path() -> Result<PathBuf, String> {
    if let Some(path) = std::env::var_os("CCP_WEBGPU_LIBRARY") { return Ok(PathBuf::from(path)); }
    let name = if cfg!(windows) { "onnxruntime_providers_webgpu.dll" }
        else if cfg!(target_os="macos") { "libonnxruntime_providers_webgpu.dylib" }
        else { "libonnxruntime_providers_webgpu.so" };
    let mut dirs = Vec::new();
    if let Some(path) = std::env::var_os("ORT_DYLIB_PATH") { if let Some(dir) = Path::new(&path).parent() { dirs.push(dir.to_path_buf()); } }
    if let Ok(exe) = std::env::current_exe() { if let Some(dir) = exe.parent() { dirs.push(dir.to_path_buf()); } }
    dirs.into_iter().map(|dir| dir.join(name)).find(|p| p.is_file())
        .ok_or_else(|| format!("Missing native WebGPU provider {name}; install a complete application package."))
}

pub fn commit_session(path: &Path, ep: GpuEp) -> Result<Session, String> {
    #[cfg(windows)]
    if ep == GpuEp::Vulkan { preload_system_vulkan()?; }
    if ep != GpuEp::DirectMl {
        require_hardware_backend(ep)?;
    }
    let mut builder = Session::builder().map_err(|e| e.to_string())?
        .with_intra_threads(runtime_assignment().threads.max(1) as usize).map_err(|e| e.to_string())?
        .with_disable_cpu_fallback().map_err(|e| e.to_string())?;
    if let Some(profile) = std::env::var_os("CCP_INFERENCE_PROFILE") {
        builder = builder.with_profiling(PathBuf::from(profile)).map_err(|e| e.to_string())?;
    }
    match ep {
        GpuEp::DirectMl => {
            if !cfg!(windows) { return Err("DirectML requires Windows; choose WebGPU/Vulkan.".into()); }
            builder = builder.with_memory_pattern(false).map_err(|e| e.to_string())?
                .with_parallel_execution(false).map_err(|e| e.to_string())?;
            let adapter = if std::env::var_os("CCP_ORT_DEVICE_ID").is_some() {
                crate::cache_key::ort_device_id()?
            } else {
                crate::probe::enumerate_gpus().into_iter().max_by_key(|g| g.vram_mb)
                    .map(|g| g.index as i32).unwrap_or(0)
            };
            eprintln!("ccp_detect: DirectML DXGI adapter index {adapter}");
            ort::ep::DirectML::default().with_device_id(adapter)
                .register(&mut builder).map_err(|e| e.to_string())?;
        }
        GpuEp::WebGpu | GpuEp::Vulkan => {
            // The ORT environment owns registrations. Do not unregister a library
            // while cached chart/plane sessions still refer to its devices.
            let library = plugin_path()?;
            let env = Environment::current().map_err(|e| e.to_string())?;
            let mut registration = REGISTERED.get_or_init(|| Mutex::new(None)).lock().map_err(|e| e.to_string())?;
            if let Some(loaded) = registration.as_ref() {
                if loaded.path != library { return Err("WebGPU library changed; restart the worker.".into()); }
            } else {
                let handle=env.register_ep_library("ccp_webgpu", &library).map_err(|e| e.to_string())?;
                *registration = Some(RegisteredLibrary { path: library, library: handle, backend: None });
            }
            let backend = dawn_backend(ep);
            claim_backend(&mut registration.as_mut().unwrap().backend, backend)?;
            let device = env.devices().filter(|d| d.ep().ok() == Some("WebGpuExecutionProvider")
                && d.hardware_device().ty() == DeviceType::GPU)
                .nth(crate::cache_key::ort_device_id()? as usize)
                .ok_or_else(|| "No compatible hardware WebGPU adapter. GPU inference cannot fall back to CPU.".to_string())?;
            eprintln!("ccp_detect: WebGPU vendor {}, device {}", device.hardware_device().vendor().unwrap_or("unknown"), device.hardware_device().id());
            let options = vec![
                ("WebGpuExecutionProvider.enableGraphCapture".into(), "0".into()),
                ("WebGpuExecutionProvider.powerPreference".into(), "high-performance".into()),
                // This is an ORT context ID, not a physical adapter index.
                // Nonzero IDs require an externally supplied WGPUDevice.
                ("WebGpuExecutionProvider.deviceId".into(), "0".into()),
                ("WebGpuExecutionProvider.dawnBackendType".into(), backend.into()),
            ];
            builder = builder.with_devices([device], Some(&options)).map_err(|e| e.to_string())?;
            // Serialize initialization as well as registration; chart and
            // plane sessions share ORT's default native WebGPU context.
            return builder.commit_from_file(path).map_err(|e| format!("{} GPU-only inference unavailable: {e}", ep.key()));
        }
    }
    builder.commit_from_file(path).map_err(|e| format!("{} GPU-only inference unavailable: {e}", ep.key()))
}

/// Dawn's native plugin can call LoadLibraryEx with DLL_LOAD_DIR and a bare
/// filename, which Windows rejects with error 87. Load the installed loader
/// through the system-only search first. Do not copy driver DLLs into packages
/// or search the current directory. Retain this one reference for the process:
/// cached native sessions and their driver threads can outlive a request.
#[cfg(windows)]
fn preload_system_vulkan() -> Result<(), String> {
    static LOADER: OnceLock<Result<usize, String>> = OnceLock::new();
    LOADER.get_or_init(|| {
        #[link(name = "kernel32")]
        extern "system" {
            fn LoadLibraryExW(name: *const u16, file: *mut std::ffi::c_void, flags: u32) -> *mut std::ffi::c_void;
        }
        let name: Vec<u16> = "vulkan-1.dll\0".encode_utf16().collect();
        // LOAD_LIBRARY_SEARCH_SYSTEM32. Windows selects the architecture-correct
        // system directory for this process, including on ARM64.
        let handle = unsafe { LoadLibraryExW(name.as_ptr(), std::ptr::null_mut(), 0x800) };
        if handle.is_null() {
            Err(format!("The system Vulkan loader is unavailable ({}). Update the graphics driver or choose Automatic.", std::io::Error::last_os_error()))
        } else { Ok(handle as usize) }
    }).as_ref().map(|_| ()).map_err(Clone::clone)
}

/// ORT may enumerate a host GPU even when the guest has only a software Vulkan
/// ICD. Verify a native graphics adapter independently before allowing WebGPU.
/// This is a prerequisite check, not a claim that ORT's device index maps to it.
fn require_hardware_backend(ep: GpuEp) -> Result<(), String> {
    use std::{future::Future, pin::pin, sync::Arc, task::{Context, Wake, Waker}};
    struct ThreadWake(std::thread::Thread);
    impl Wake for ThreadWake { fn wake(self: Arc<Self>) { self.0.unpark(); } }
    let backend = if ep == GpuEp::Vulkan || cfg!(target_os="linux") {
        wgpu::Backends::VULKAN
    } else if cfg!(target_os="macos") {
        wgpu::Backends::METAL
    } else { wgpu::Backends::DX12 };
    let mut descriptor=wgpu::InstanceDescriptor::new_without_display_handle();
    // Explicit WGPU_* opt-ins are useful for non-conformant VM translation
    // drivers. Hardware filtering below still rejects CPU/software adapters.
    descriptor.flags=descriptor.flags.with_env();
    let instance=wgpu::Instance::new(descriptor);
    let mut future=pin!(instance.enumerate_adapters(backend));
    let waker=Waker::from(Arc::new(ThreadWake(std::thread::current())));
    let mut context=Context::from_waker(&waker);
    let adapters=loop {
        match future.as_mut().poll(&mut context) {
            std::task::Poll::Ready(adapters) => break adapters,
            std::task::Poll::Pending => std::thread::park(),
        }
    };
    let hardware:Vec<_>=adapters.into_iter().map(|adapter|adapter.get_info())
        .filter(|info|matches!(info.device_type,wgpu::DeviceType::DiscreteGpu|wgpu::DeviceType::IntegratedGpu)).collect();
    if hardware.is_empty() {
        return Err(format!("No hardware {backend:?} adapter available for {} inference. Software graphics drivers and CPU inference fallback are disabled.",ep.key()));
    }
    eprintln!("ccp_detect: hardware graphics prerequisite: {}",hardware.iter().map(|info|info.name.as_str()).collect::<Vec<_>>().join(", "));
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn every_allowed_provider_is_gpu_only() {
        for ep in [GpuEp::DirectMl, GpuEp::WebGpu, GpuEp::Vulkan] {
            assert!(matches!(ep.key(), "directml" | "webgpu" | "vulkan"));
        }
    }
    #[test]
    fn backend_change_requires_restart_but_same_backend_can_share_context() {
        let mut backend = None;
        claim_backend(&mut backend, "Vulkan").unwrap();
        claim_backend(&mut backend, "Vulkan").unwrap();
        assert!(claim_backend(&mut backend, "D3D12").unwrap_err().contains("restart"));
        assert_eq!(backend, Some("Vulkan"));
    }
    #[test]
    fn explicit_vulkan_never_uses_a_platform_default_backend() {
        assert_eq!(dawn_backend(GpuEp::Vulkan), "Vulkan");
        if cfg!(target_os = "linux") {
            assert_eq!(dawn_backend(GpuEp::WebGpu), "Vulkan");
        }
    }
}
