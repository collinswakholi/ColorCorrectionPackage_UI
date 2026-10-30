//! Explicit inference sessions and bounded Auto recovery for chart and plane networks.
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use ort::{environment::Environment, memory::DeviceType, session::Session};
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
pub enum GpuEp { DirectMl, WebGpu, Vulkan, Cpu }
impl GpuEp {
    pub fn key(self) -> &'static str { match self { Self::DirectMl => "directml", Self::WebGpu => "webgpu", Self::Vulkan => "vulkan", Self::Cpu => "cpu" } }
}

fn dawn_backend(ep: GpuEp) -> &'static str {
    dawn_backend_for(ep, std::env::consts::OS)
}

fn dawn_backend_for(ep: GpuEp, target_os: &str) -> &'static str {
    if ep == GpuEp::Vulkan || target_os == "linux" { "Vulkan" }
    else if target_os == "macos" { "Metal" }
    else { "D3D12" }
}

fn webgpu_options(backend: &str) -> Result<Vec<(String, String)>, String> {
    // ORT 1.24.4 ParseWebGpuContextConfig accepts only D3D12/Vulkan overrides.
    // Metal is selected by Dawn's platform default; passing "Metal" can abort
    // the native plugin instead of returning an error across the C API.
    let explicit_backend = match backend {
        "D3D12" => Some("D3D12"),
        "Vulkan" => Some("Vulkan"),
        "Metal" => None,
        other => return Err(format!("Unsupported WebGPU graphics backend: {other}. Choose Auto or a supported detection engine.")),
    };
    let mut options = vec![
        ("WebGpuExecutionProvider.enableGraphCapture".into(), "0".into()),
        ("WebGpuExecutionProvider.powerPreference".into(), "high-performance".into()),
        // This is an ORT context ID, not a physical adapter index.
        // Nonzero IDs require an externally supplied WGPUDevice.
        ("WebGpuExecutionProvider.deviceId".into(), "0".into()),
    ];
    if let Some(value) = explicit_backend {
        options.push(("WebGpuExecutionProvider.dawnBackendType".into(), value.into()));
    }
    Ok(options)
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
    let policy=runtime_policy();
    let mut order=provider_order_for(policy);
    if matches!(policy,DevicePolicy::Auto|DevicePolicy::Cpu) {order.retain(|ep|component_enabled(*ep));}
    order
}

/// Installed selection controls activation, never the mandatory CPU provider.
pub fn component_policy_identity() -> String {crate::probe::selected_components()}

fn component_enabled(ep: GpuEp) -> bool {
    ep==GpuEp::Cpu || component_policy_identity().split(',').any(|item|item.trim()==match ep {
        GpuEp::DirectMl=>"directml",GpuEp::Vulkan|GpuEp::WebGpu=>"webgpu",GpuEp::Cpu=>"cpu"
    })
}

fn provider_order_for(policy: DevicePolicy) -> Vec<GpuEp> {
    match policy {
        DevicePolicy::CpuInference => vec![GpuEp::Cpu],
        DevicePolicy::DirectMl => vec![GpuEp::DirectMl],
        DevicePolicy::WebGpu => vec![GpuEp::WebGpu],
        DevicePolicy::Vulkan => vec![GpuEp::Vulkan],
        _ if cfg!(windows) => vec![GpuEp::DirectMl, GpuEp::WebGpu, GpuEp::Cpu],
        _ => vec![GpuEp::WebGpu, GpuEp::Cpu],
    }
}

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
pub struct Attempt { pub backend: String, pub reason: String }

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
pub struct ExecutionRecord {
    pub requested_policy: String,
    pub backend: String,
    pub provider: String,
    pub adapter: Option<serde_json::Value>,
    pub threads: Option<u32>,
    pub cached: bool,
    pub fallback: Option<serde_json::Value>,
    pub attempts: Vec<Attempt>,
}

pub struct CachedInference {
    model_identity: String,
    policy: String,
    ep: GpuEp,
    session: InferenceSession,
    pub record: ExecutionRecord,
}

/// Error origin, rather than OS wording, determines whether Auto may retry.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum FailureKind { Provider, Asset, Input, Cancelled }
#[derive(Debug)]
struct InferenceFailure { kind: FailureKind, reason: String }
impl InferenceFailure {
    fn provider(reason: impl Into<String>) -> Self { Self {kind:FailureKind::Provider,reason:reason.into()} }
    fn asset(reason: impl Into<String>) -> Self { Self {kind:FailureKind::Asset,reason:reason.into()} }
    fn execution(reason: String) -> Self {
        let lower=reason.to_ascii_lowercase();
        let kind=if lower.contains("cancelled") || lower.contains("canceled") {FailureKind::Cancelled}
            else if lower.contains("invalid input") || lower.contains("invalid inference input") {FailureKind::Input}
            else {FailureKind::Provider};
        Self{kind,reason}
    }
    fn model_session(error: ort::Error, ep: GpuEp) -> Self {
        let kind=Self::model_category(error.code(),error.message());
        Self{kind,reason:format!("{} inference unavailable: {error}",ep.key())}
    }
    fn model_category(code:ort::ErrorCode,message:&str)->FailureKind {
        let lower=message.to_ascii_lowercase();
        if matches!(code,ort::ErrorCode::NoSuchFile|ort::ErrorCode::NoModel|ort::ErrorCode::InvalidProtobuf|ort::ErrorCode::InvalidGraph)
            || ["protobuf parsing failed","unsupported model ir version","external data"].iter().any(|hint|lower.contains(hint)) {
            FailureKind::Asset
        } else {FailureKind::Provider}
    }
}
impl From<String> for InferenceFailure {fn from(reason:String)->Self {Self::provider(reason)}}
impl From<&str> for InferenceFailure {fn from(reason:&str)->Self {Self::provider(reason)}}
impl std::fmt::Display for InferenceFailure {
    fn fmt(&self,f:&mut std::fmt::Formatter<'_>)->std::fmt::Result {f.write_str(&self.reason)}
}

fn wsl_addon_failure_context(ep: GpuEp, mut failure: InferenceFailure,
    addon_error: Option<&str>) -> InferenceFailure {
    if ep==GpuEp::Cpu || failure.kind!=FailureKind::Provider {return failure;}
    let bounded: String=addon_error.unwrap_or("").chars().take(512)
        .map(|c|if c.is_control() || c.is_whitespace() {' '} else {c}).collect();
    let detail=bounded.split_whitespace().collect::<Vec<_>>().join(" ");
    if !detail.is_empty() {
        failure.reason.push_str(&format!("; WSL GPU add-on unavailable: {detail}. Run Check setup to repair or disable the optional WSL add-on."));
    }
    failure
}

/// A failed optional add-on is context for a failed GPU attempt, not proof
/// that every native GPU route is unusable. Successful attempts are untouched.
fn gpu_failure_context(ep: GpuEp, failure: InferenceFailure) -> InferenceFailure {
    if ep==GpuEp::Cpu || failure.kind!=FailureKind::Provider {return failure;}
    let addon_error=std::env::var("CCP_WSL_GPU_ADDON_ERROR").ok();
    wsl_addon_failure_context(ep,failure,addon_error.as_deref())
}

fn preflight_model(path: &Path) -> Result<(),InferenceFailure> {
    let file=std::fs::File::open(path).map_err(|e|InferenceFailure::asset(format!("Cannot read inference model {}: {e}",path.display())))?;
    if !file.metadata().map_err(|e|InferenceFailure::asset(e.to_string()))?.is_file() {
        return Err(InferenceFailure::asset(format!("Inference model is not a regular file: {}",path.display())));
    }
    Ok(())
}

static QUARANTINE: OnceLock<Mutex<Vec<(String, GpuEp, String)>>> = OnceLock::new();
pub fn reset_failures() {
    if let Some(lock)=QUARANTINE.get() {if let Ok(mut failures)=lock.lock() {failures.clear();}}
}
fn device_failure(reason: &str) -> bool {
    let reason=reason.to_ascii_lowercase();
    ["no hardware", "no compatible hardware", "software renderer", "adapter", "device lost", "device removed",
        "missing native webgpu", "system vulkan loader", "graphics driver", "webgpu library changed"]
        .iter().any(|hint|reason.contains(hint))
}
fn quarantine(policy: &str, ep: GpuEp, reason: &str) {
    if ep!=GpuEp::Cpu && device_failure(reason) {
        if let Ok(mut failures)=QUARANTINE.get_or_init(||Mutex::new(Vec::new())).lock() {
            if !failures.iter().any(|(p,e,_)|p==policy && *e==ep) {failures.push((policy.into(),ep,reason.into()));}
        }
    }
}

/// Shared bounded attempt loop, independently tested without any graphics runtime.
fn try_candidates<T>(candidates: Vec<GpuEp>, mut failures: Vec<Attempt>,
    mut run: impl FnMut(GpuEp)->Result<T,InferenceFailure>) -> Result<(GpuEp,T,Vec<Attempt>),String> {
    let mut tried=Vec::new();
    for ep in candidates {
        if tried.contains(&ep) {continue;}
        tried.push(ep);
        match run(ep) {
            Ok(value)=>return Ok((ep,value,failures)),
            Err(failure)=>{
                if failure.kind!=FailureKind::Provider {return Err(failure.reason);}
                failures.push(Attempt{backend:ep.key().into(),reason:failure.reason});
            }
        }
    }
    Err(format!("Inference unavailable: {}",failures.iter().map(|a|format!("{}: {}",a.backend,a.reason)).collect::<Vec<_>>().join("; ")))
}

fn make_record(ep: GpuEp, session: &InferenceSession, attempts: Vec<Attempt>) -> ExecutionRecord {
    let backend = session.backend(ep);
    let provider = if backend.contains(":wgpu:") { "RustWgpu" } else { match ep {
        GpuEp::Cpu => "CPUExecutionProvider", GpuEp::DirectMl => "DmlExecutionProvider",
        _ => "WebGpuExecutionProvider",
    }};
    let adapter = if ep == GpuEp::DirectMl {
        let all = crate::probe::enumerate_gpus();
        let selected = std::env::var_os("CCP_ORT_DEVICE_ID").and_then(|_| crate::cache_key::ort_device_id().ok());
        all.iter().filter(|gpu| selected.is_none_or(|index|gpu.index as i32 == index)).max_by_key(|gpu|gpu.vram_mb)
            .map(|gpu|serde_json::json!({"name":gpu.name,"index":gpu.index,"backend":"dxgi","identity_confidence":"selected_index"}))
    } else if ep == GpuEp::Cpu { None } else {
        // The native prerequisite enumeration does not prove the plugin's physical selection.
        Some(serde_json::json!({"backend":backend,"identity_confidence":"provider_only"}))
    };
    let fallback = attempts.first().map(|first|serde_json::json!({"from":first.backend,
        "reason":attempts.iter().map(|a|format!("{}: {}",a.backend,a.reason)).collect::<Vec<_>>().join("; ")}));
    ExecutionRecord { requested_policy: runtime_policy().as_str().into(), backend, provider:provider.into(),
        adapter, threads:if ep==GpuEp::Cpu {Some(runtime_assignment().threads.clamp(1,8))} else {None},
        cached:false, fallback, attempts }
}

/// Retry only this inference invocation. Input ownership stays with the caller,
/// and cached plugin libraries outlive failed sessions. No cross-cache locks.
pub fn execute<T>(cache: &mut Option<CachedInference>, path: &Path,
    mut run: impl FnMut(&mut InferenceSession, &str) -> Result<T,String>) -> Result<T,String> {
    preflight_model(path).map_err(|failure|failure.reason)?;
    let identity = crate::cache_key::model_cache_identity(path);
    let policy = crate::cache_key::runtime_cache_identity();
    if cache.as_ref().is_some_and(|c|c.model_identity != identity || c.policy != policy) { *cache=None; }
    let mut attempted = Vec::new();
    let mut failures = Vec::new();
    if let Some(active) = cache.as_mut() {
        attempted.push(active.ep);
        match run(&mut active.session, &active.record.backend) {
            Ok(result) => { active.record.cached=true; return Ok(result); }
            Err(reason) => {
                let failure=InferenceFailure::execution(reason);
                if failure.kind!=FailureKind::Provider {return Err(failure.reason);}
                quarantine(&policy,active.ep,&failure.reason);
                let reason=gpu_failure_context(active.ep,failure).reason;
                failures = active.record.attempts.clone();
                failures.push(Attempt{backend:active.ep.key().into(),reason});
                // Previous initialization failures are already accounted for.
                for ep in provider_order() {
                    if failures.iter().any(|failure|failure.backend==ep.key()) { attempted.push(ep); }
                }
            }
        }
        *cache=None;
    }
    if let Some(lock)=QUARANTINE.get() {
        if let Ok(quarantined)=lock.lock() {
            for (p,ep,reason) in quarantined.iter().filter(|(p,_,_)|p==&policy) {
                let _=p;
                if !attempted.contains(ep) {
                    attempted.push(*ep);
                    let reason=gpu_failure_context(*ep,InferenceFailure::provider(reason.clone())).reason;
                    failures.push(Attempt{backend:ep.key().into(),reason});
                }
            }
        }
    }
    let candidates=provider_order().into_iter().filter(|ep|!attempted.contains(ep)).collect();
    let (ep,(session,result),failures)=try_candidates(candidates,failures,|ep| {
        let result = commit_inference_session_classified(path,ep).and_then(|mut session| {
            let backend=session.backend(ep);
            let result=run(&mut session,&backend).map_err(InferenceFailure::execution)?;
            Ok((session,result))
        });
        result.map_err(|failure| {
            if failure.kind==FailureKind::Provider {quarantine(&policy,ep,&failure.reason);}
            gpu_failure_context(ep,failure)
        })
    })?;
    let record=make_record(ep,&session,failures);
    *cache=Some(CachedInference{model_identity:identity,policy,ep,session,record});
    Ok(result)
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
    commit_inference_session_classified(path,ep).map_err(|failure|failure.reason)
}
fn commit_inference_session_classified(path: &Path, ep: GpuEp) -> Result<InferenceSession,InferenceFailure> {
    preflight_model(path)?;
    if !component_enabled(ep) {return Err(format!("{} inference is disabled by installation choices. Open Check setup to enable it, or select CPU.",ep.key()).into());}
    #[cfg(all(target_os="linux",feature="rust-inference"))]
    if matches!(ep, GpuEp::WebGpu | GpuEp::Vulkan) && Path::new("/dev/dxg").exists() {
        // WSL's Dozen lacks fullDrawIndexUint32. Dawn requires this drawing
        // feature even for ONNX compute; Rust/wgpu needs only actual compute
        // capabilities. Native Linux keeps the established ONNX provider.
        return ccp_wgpu_infer::Session::new(path).map(InferenceSession::Rust).map_err(InferenceFailure::provider);
    }
    commit_session_classified(path,ep).map(InferenceSession::Ort)
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
    preflight_model(path).map_err(|failure|failure.reason)?;
    commit_session_classified(path,ep).map_err(|failure|failure.reason)
}
fn commit_session_classified(path: &Path, ep: GpuEp) -> Result<Session, InferenceFailure> {
    #[cfg(windows)]
    if ep == GpuEp::Vulkan { preload_system_vulkan()?; }
    if matches!(ep, GpuEp::WebGpu | GpuEp::Vulkan) {
        require_hardware_backend(ep)?;
    }
    let mut builder = Session::builder().map_err(|e| e.to_string())?
        .with_intra_threads(runtime_assignment().threads.clamp(1,8) as usize).map_err(|e| e.to_string())?
        .with_parallel_execution(false).map_err(|e| e.to_string())?
        .with_intra_op_spinning(false).map_err(|e| e.to_string())?
        .with_inter_op_spinning(false).map_err(|e| e.to_string())?;
    if ep != GpuEp::Cpu {
        builder = builder.with_disable_cpu_fallback().map_err(|e| e.to_string())?;
    }
    if let Some(profile) = std::env::var_os("CCP_INFERENCE_PROFILE") {
        builder = builder.with_profiling(PathBuf::from(profile)).map_err(|e| e.to_string())?;
    }
    match ep {
        GpuEp::Cpu => {
            // rc.13's CPU registration only changes arena settings. Append the
            // actual CPU device too, which overrides the default MaxEfficiency
            // selection even when a failed GPU plugin remains registered.
            builder = builder.with_no_environment_execution_providers().map_err(|e|e.to_string())?
                .with_execution_providers([ort::ep::CPU::default().build().error_on_failure()])
                .map_err(|e| e.to_string())?;
            let environment=Environment::current().map_err(|e|e.to_string())?;
            let devices=environment.devices().filter(|device|device.ep().ok()==Some("CPUExecutionProvider")
                && device.hardware_device().ty()==DeviceType::CPU).collect::<Vec<_>>();
            if devices.is_empty() {return Err("The installed ONNX Runtime has no CPU inference device. Repair the application package.".into());}
            builder=builder.with_devices(devices,None).map_err(|e|e.to_string())?;
        }
        GpuEp::DirectMl => {
            if !cfg!(windows) { return Err("DirectML requires Windows; choose WebGPU/Vulkan.".into()); }
            builder = builder.with_memory_pattern(false).map_err(|e| e.to_string())?
                .with_parallel_execution(false).map_err(|e| e.to_string())?;
            let adapters = crate::probe::enumerate_gpus();
            let adapter = if std::env::var_os("CCP_ORT_DEVICE_ID").is_some() {
                let requested = crate::cache_key::ort_device_id().map_err(|reason|InferenceFailure{kind:FailureKind::Input,reason})?;
                if !adapters.iter().any(|gpu| gpu.index as i32 == requested) {
                    return Err(format!("DirectML adapter {requested} is unavailable or is a software renderer.").into());
                }
                requested
            } else {
                adapters.iter().max_by_key(|g| g.vram_mb).map(|g| g.index as i32)
                    .ok_or("DirectML unavailable: no hardware DXGI adapter; software renderers are excluded.")?
            };
            eprintln!("ccp_detect: DirectML DXGI adapter index {adapter}");
            builder = builder.with_execution_providers([ort::ep::DirectML::default().with_device_id(adapter).build().error_on_failure()])
                .map_err(|e|e.to_string())?;
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
            let options = webgpu_options(backend)?;
            claim_backend(&mut registration.as_mut().unwrap().backend, backend)?;
            let device = env.devices().filter(|d| d.ep().ok() == Some("WebGpuExecutionProvider")
                && d.hardware_device().ty() == DeviceType::GPU)
                .nth(crate::cache_key::ort_device_id().map_err(|reason|InferenceFailure{kind:FailureKind::Input,reason})? as usize)
                .ok_or_else(|| "No compatible WebGPU device was exposed by the inference runtime.".to_string())?;
            eprintln!("ccp_detect: WebGPU vendor {}, device {}", device.hardware_device().vendor().unwrap_or("unknown"), device.hardware_device().id());
            builder = builder.with_devices([device], Some(&options)).map_err(|e| e.to_string())?;
            // Serialize initialization as well as registration; chart and
            // plane sessions share ORT's default native WebGPU context.
            return builder.commit_from_file(path).map_err(|error|InferenceFailure::model_session(error,ep));
        }
    }
    builder.commit_from_file(path).map_err(|error|InferenceFailure::model_session(error,ep))
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
        .filter(|info|crate::probe::eligible_wgpu_adapter(info)).collect();
    if hardware.is_empty() {
        return Err(format!("No hardware {backend:?} adapter available for {} inference. Software renderers cannot provide GPU inference; choose Auto or CPU.",ep.key()));
    }
    eprintln!("ccp_detect: hardware graphics prerequisite: {}",hardware.iter().map(|info|info.name.as_str()).collect::<Vec<_>>().join(", "));
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cpu_is_explicit_and_legacy_cpu_keeps_auto_inference() {
        assert_eq!(provider_order_for(DevicePolicy::CpuInference),vec![GpuEp::Cpu]);
        assert_eq!(provider_order_for(DevicePolicy::Cpu),provider_order_for(DevicePolicy::Auto));
        assert_eq!(provider_order_for(DevicePolicy::Auto).last(),Some(&GpuEp::Cpu));
        assert_eq!(provider_order_for(DevicePolicy::DirectMl),vec![GpuEp::DirectMl]);
    }
    #[test]
    fn execution_failure_and_malformed_output_retry_once_then_cpu() {
        let mut called=Vec::new();
        let (ep,value,failures)=try_candidates(vec![GpuEp::DirectMl,GpuEp::DirectMl,GpuEp::WebGpu,GpuEp::Cpu],Vec::new(),|ep| {
            called.push(ep);
            match ep {GpuEp::DirectMl=>Err("device lost during execution".into()),
                GpuEp::WebGpu=>Err("malformed detector output".into()),_=>Ok(vec![1.0f32])}
        }).unwrap();
        assert_eq!(called,vec![GpuEp::DirectMl,GpuEp::WebGpu,GpuEp::Cpu]);
        assert_eq!(ep,GpuEp::Cpu);assert_eq!(value,vec![1.0]);assert_eq!(failures.len(),2);
    }
    #[test]
    fn forced_engine_does_not_retry_and_asset_errors_are_fatal() {
        let mut called=Vec::new();
        assert!(try_candidates::<()>(vec![GpuEp::WebGpu],Vec::new(),|ep|{called.push(ep);Err("device lost".into())}).is_err());
        assert_eq!(called,vec![GpuEp::WebGpu]);
        called.clear();
        assert!(try_candidates::<()>(vec![GpuEp::WebGpu,GpuEp::Cpu],Vec::new(),|ep|{called.push(ep);Err(InferenceFailure::asset("Failed to load model: external data missing"))}).is_err());
        assert_eq!(called,vec![GpuEp::WebGpu]);
        assert_eq!(InferenceFailure::execution("cancelled".into()).kind,FailureKind::Cancelled);
    }
    #[test]
    fn missing_or_denied_gpu_dependencies_fall_back_but_assets_do_not() {
        for message in ["GPU library: No such file or directory", "Vulkan driver: Permission denied"] {
            let mut called=Vec::new();
            let (ep,_,attempts)=try_candidates(vec![GpuEp::WebGpu,GpuEp::Cpu],Vec::new(),|ep| {
                called.push(ep);
                if ep==GpuEp::WebGpu {Err(InferenceFailure::provider(message))} else {Ok(())}
            }).unwrap();
            assert_eq!(ep,GpuEp::Cpu);assert_eq!(called,vec![GpuEp::WebGpu,GpuEp::Cpu]);
            assert_eq!(attempts[0].reason,message);
        }
        let mut called=Vec::new();
        assert!(try_candidates::<()>(vec![GpuEp::WebGpu,GpuEp::Cpu],Vec::new(),|ep| {
            called.push(ep);Err(InferenceFailure::asset("Model data: Permission denied"))
        }).is_err());
        assert_eq!(called,vec![GpuEp::WebGpu]);
    }
    #[test]
    fn failed_wsl_addon_does_not_change_cpu_or_asset_errors() {
        let addon=Some("Enabled marker exists but the optional payload is missing");
        let cpu=wsl_addon_failure_context(GpuEp::Cpu,InferenceFailure::provider("CPU execution failed"),addon);
        assert_eq!(cpu.reason,"CPU execution failed");
        let asset=wsl_addon_failure_context(GpuEp::WebGpu,InferenceFailure::asset("Model file is missing"),addon);
        assert_eq!(asset.kind,FailureKind::Asset);
        assert_eq!(asset.reason,"Model file is missing");
        let absent=wsl_addon_failure_context(GpuEp::WebGpu,InferenceFailure::provider("No hardware adapter"),None);
        assert_eq!(absent.reason,"No hardware adapter");
    }
    #[test]
    fn failed_wsl_addon_is_reported_in_auto_recovery_and_forced_gpu_failure() {
        let addon=Some("Optional WSL GPU payload is missing");
        let mut calls=Vec::new();
        let (ep,_,attempts)=try_candidates(vec![GpuEp::WebGpu,GpuEp::Cpu],Vec::new(),|ep| {
            calls.push(ep);
            if ep==GpuEp::WebGpu {
                Err(wsl_addon_failure_context(ep,InferenceFailure::provider("No compatible hardware Vulkan adapter"),addon))
            } else {Ok(())}
        }).unwrap();
        assert_eq!(calls,vec![GpuEp::WebGpu,GpuEp::Cpu]);
        assert_eq!(ep,GpuEp::Cpu);
        assert!(attempts[0].reason.contains("Optional WSL GPU payload is missing"));
        assert!(attempts[0].reason.contains("Run Check setup"));
        let error=try_candidates::<()>(vec![GpuEp::WebGpu],Vec::new(),|ep| {
            Err(wsl_addon_failure_context(ep,InferenceFailure::provider("No compatible hardware Vulkan adapter"),addon))
        }).unwrap_err();
        assert!(error.contains("WSL GPU add-on unavailable"));
        assert!(error.contains("repair or disable"));
        // The optional failure must not reject a working ordinary GPU route.
        let (ep,_,attempts)=try_candidates(vec![GpuEp::WebGpu,GpuEp::Cpu],Vec::new(),|ep| {
            Ok::<_,InferenceFailure>(()).map_err(|failure|wsl_addon_failure_context(ep,failure,addon))
        }).unwrap();
        assert_eq!(ep,GpuEp::WebGpu);
        assert!(attempts.is_empty());
    }
    #[test]
    fn optional_wsl_addon_diagnostics_are_bounded_and_single_line() {
        let detail="é\n\t\u{1b}[31m missing payload ".repeat(1000);
        let failure=wsl_addon_failure_context(GpuEp::WebGpu,InferenceFailure::provider("No hardware adapter"),Some(&detail));
        assert!(failure.reason.chars().count()<700);
        assert!(!failure.reason.chars().any(char::is_control));
        assert!(failure.reason.contains("Check setup"));
    }
    #[test]
    fn missing_model_is_rejected_before_any_provider_is_initialized() {
        let path=std::env::temp_dir().join(format!("ccp-no-such-model-{}.onnx",std::process::id()));
        let mut cache=None;let mut executions=0;
        let error=execute(&mut cache,&path,|_,_|{executions+=1;Ok(())}).unwrap_err();
        assert!(error.contains("Cannot read inference model"));assert_eq!(executions,0);assert!(cache.is_none());
        for code in [ort::ErrorCode::InvalidProtobuf,ort::ErrorCode::InvalidGraph,ort::ErrorCode::NoSuchFile] {
            assert_eq!(InferenceFailure::model_category(code,"Model asset invalid"),FailureKind::Asset);
        }
    }
    #[test]
    fn gpu_provider_names_remain_compatible() {
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
    fn macos_webgpu_uses_dawn_default_without_an_invalid_metal_override() {
        let backend=dawn_backend_for(GpuEp::WebGpu,"macos");
        assert_eq!(backend,"Metal");
        let options=webgpu_options(backend).unwrap();
        assert!(!options.iter().any(|(key,_)|key.ends_with("dawnBackendType")));
        assert!(options.iter().any(|(key,value)|key.ends_with("powerPreference") && value=="high-performance"));
        assert!(options.iter().any(|(key,value)|key.ends_with("enableGraphCapture") && value=="0"));
        // Context ownership still distinguishes the default Metal context.
        let mut claimed=None;
        claim_backend(&mut claimed,backend).unwrap();
        assert!(claim_backend(&mut claimed,"Vulkan").is_err());
    }
    #[test]
    fn explicit_dawn_overrides_keep_the_pinned_runtime_spelling_on_all_platforms() {
        for (platform,ep,expected) in [
            ("windows",GpuEp::WebGpu,"D3D12"),
            ("windows",GpuEp::Vulkan,"Vulkan"),
            ("linux",GpuEp::WebGpu,"Vulkan"),
            ("linux",GpuEp::Vulkan,"Vulkan"),
            ("macos",GpuEp::Vulkan,"Vulkan"),
        ] {
            let options=webgpu_options(dawn_backend_for(ep,platform)).unwrap();
            assert_eq!(options.iter().find(|(key,_)|key.ends_with("dawnBackendType")).map(|(_,value)|value.as_str()),Some(expected));
        }
    }
    #[test]
    fn invalid_native_backend_options_fail_before_the_provider_and_auto_can_retry_cpu() {
        for backend in ["metal","d3d12","vulkan","OpenGL"] {
            let mut calls=Vec::new();
            let result=try_candidates(vec![GpuEp::WebGpu,GpuEp::Cpu],Vec::new(),|ep| {
                calls.push(ep);
                if ep==GpuEp::WebGpu {webgpu_options(backend).map_err(InferenceFailure::provider)?;}
                Ok(())
            }).unwrap();
            assert_eq!(result.0,GpuEp::Cpu);
            assert_eq!(calls,vec![GpuEp::WebGpu,GpuEp::Cpu]);
            assert!(result.2[0].reason.contains("Unsupported WebGPU graphics backend"));
            let error=try_candidates(vec![GpuEp::WebGpu],Vec::new(),|_| {
                webgpu_options(backend).map_err(InferenceFailure::provider)
            }).unwrap_err();
            assert!(error.contains("Unsupported WebGPU graphics backend"));
        }
    }
    #[test]
    fn explicit_vulkan_never_uses_a_platform_default_backend() {
        assert_eq!(dawn_backend(GpuEp::Vulkan), "Vulkan");
        if cfg!(target_os = "linux") {
            assert_eq!(dawn_backend(GpuEp::WebGpu), "Vulkan");
        }
    }
}
