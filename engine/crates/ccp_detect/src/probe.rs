//! Accelerator probe: auto-detect CUDA, DirectML, OpenVINO, MPS, MLX, and CPU.

use std::env;
use std::path::PathBuf;
use std::sync::Mutex;
use std::thread::available_parallelism;

use serde::{Deserialize, Serialize};

use crate::paths::{
    default_libtorch_root, libtorch_version_matches_29, read_libtorch_json, LibTorchJson,
};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BackendKind {
    LibTorchCpu,
    LibTorchCuda,
    LibTorchMps,
    OrtCuda,
    OrtDirectMl,
    OrtOpenVino,
    OrtCoreMl,
    Mlx,
}

impl BackendKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::LibTorchCpu => "LibTorchCpu",
            Self::LibTorchCuda => "LibTorchCuda",
            Self::LibTorchMps => "LibTorchMps",
            Self::OrtCuda => "OrtCuda",
            Self::OrtDirectMl => "OrtDirectMl",
            Self::OrtOpenVino => "OrtOpenVino",
            Self::OrtCoreMl => "OrtCoreMl",
            Self::Mlx => "Mlx",
        }
    }

    pub fn short(self) -> &'static str {
        match self {
            Self::LibTorchCpu => "CPU",
            Self::LibTorchCuda | Self::OrtCuda => "CUDA",
            Self::LibTorchMps => "MPS",
            Self::OrtDirectMl => "DirectML",
            Self::OrtOpenVino => "OpenVINO",
            Self::OrtCoreMl => "CoreML",
            Self::Mlx => "MLX",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CpuInfo {
    pub vendor: String,
    pub brand: String,
    pub cores_logical: u32,
    pub cores_physical: u32,
    pub simd: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GpuInfo {
    pub vendor: String,
    pub name: String,
    pub vram_mb: u64,
    pub index: u32,
    pub driver: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BackendInfo {
    pub kind: BackendKind,
    pub status: String,
    pub device_index: i32,
    pub used_for: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fail_reason: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LibTorchInfo {
    pub kind: String,
    pub version: String,
    pub root: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AcceleratorReport {
    pub cpu: CpuInfo,
    pub gpus: Vec<GpuInfo>,
    pub backends: Vec<BackendInfo>,
    pub active: BackendKind,
    pub libtorch: LibTorchInfo,
    pub policy: String,
    pub assignment: ComputeAssignment,
    pub recommended: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ComputeAssignment {
    pub detect: String,
    pub plane: String,
    pub apply: String,
    pub threads: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DevicePolicy {
    Auto,
    Cpu,
    Cuda,
    DirectMl,
    OpenVino,
    Mlx,
}

impl DevicePolicy {
    pub fn parse(s: &str) -> Self {
        match s.trim().to_ascii_lowercase().as_str() {
            "cpu" => Self::Cpu,
            "cuda" | "gpu" => Self::Cuda,
            "directml" | "dml" => Self::DirectMl,
            "openvino" | "ov" | "intel" => Self::OpenVino,
            "mlx" | "metal" | "mps" => Self::Mlx,
            _ => Self::Auto,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Auto => "auto",
            Self::Cpu => "cpu",
            Self::Cuda => "cuda",
            Self::DirectMl => "directml",
            Self::OpenVino => "openvino",
            Self::Mlx => "mlx",
        }
    }
}

impl Default for ComputeAssignment {
    fn default() -> Self {
        Self {
            detect: "cpu".into(),
            plane: "cpu".into(),
            apply: "cpu".into(),
            threads: 4,
        }
    }
}

pub fn apply_thread_count(cpu: &CpuInfo) -> u32 {
    let n = cpu.cores_logical.max(1);
    n.saturating_sub(1).clamp(2, 8)
}

struct RuntimeAccel {
    policy: String,
    assignment: ComputeAssignment,
}

static RUNTIME: Mutex<RuntimeAccel> = Mutex::new(RuntimeAccel {
    policy: String::new(),
    assignment: ComputeAssignment {
        detect: String::new(),
        plane: String::new(),
        apply: String::new(),
        threads: 4,
    },
});

/// Remember the last probe so plane crop and LUT apply use the same plan.
pub fn set_runtime_from_report(report: &AcceleratorReport) {
    if let Ok(mut g) = RUNTIME.lock() {
        g.policy = report.policy.clone();
        g.assignment = report.assignment.clone();
    }
}

pub fn runtime_policy() -> DevicePolicy {
    RUNTIME
        .lock()
        .ok()
        .map(|g| {
            if g.policy.is_empty() {
                DevicePolicy::Auto
            } else {
                DevicePolicy::parse(&g.policy)
            }
        })
        .unwrap_or(DevicePolicy::Auto)
}

pub fn runtime_assignment() -> ComputeAssignment {
    RUNTIME
        .lock()
        .ok()
        .map(|g| {
            if g.assignment.detect.is_empty() {
                ComputeAssignment::default()
            } else {
                g.assignment.clone()
            }
        })
        .unwrap_or_default()
}

fn simd_label() -> String {
    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    {
        if is_x86_feature_detected!("avx512f") {
            return "avx512".into();
        }
        if is_x86_feature_detected!("avx2") {
            return "avx2".into();
        }
        if is_x86_feature_detected!("avx") {
            return "avx".into();
        }
        if is_x86_feature_detected!("sse2") {
            return "sse2".into();
        }
    }
    #[cfg(target_arch = "aarch64")]
    {
        return "neon".into();
    }
    #[allow(unreachable_code)]
    "unknown".into()
}

fn logical_cores() -> u32 {
    available_parallelism().map(|n| n.get() as u32).unwrap_or(1)
}

fn cpu_info() -> CpuInfo {
    let cores = logical_cores();
    #[cfg(windows)]
    {
        let brand = env::var("PROCESSOR_IDENTIFIER").unwrap_or_else(|_| "Unknown CPU".into());
        let vendor = if brand.to_ascii_lowercase().contains("intel") {
            "Intel"
        } else if brand.to_ascii_lowercase().contains("amd") {
            "AMD"
        } else {
            "unknown"
        };
        return CpuInfo {
            vendor: vendor.into(),
            brand,
            cores_logical: cores,
            cores_physical: cores,
            simd: simd_label(),
        };
    }
    #[cfg(target_os = "macos")]
    {
        let brand = sysctl_brand().unwrap_or_else(|| "Apple Silicon".into());
        return CpuInfo {
            vendor: "Apple".into(),
            brand,
            cores_logical: cores,
            cores_physical: cores,
            simd: simd_label(),
        };
    }
    #[cfg(target_os = "linux")]
    {
        let (vendor, brand) = linux_cpu_brand();
        return CpuInfo {
            vendor,
            brand,
            cores_logical: cores,
            cores_physical: cores,
            simd: simd_label(),
        };
    }
    #[allow(unreachable_code)]
    CpuInfo {
        vendor: "unknown".into(),
        brand: "unknown".into(),
        cores_logical: cores,
        cores_physical: cores,
        simd: simd_label(),
    }
}

#[cfg(target_os = "linux")]
fn linux_cpu_brand() -> (String, String) {
    let text = std::fs::read_to_string("/proc/cpuinfo").unwrap_or_default();
    let brand = text
        .lines()
        .find_map(|line| {
            line.strip_prefix("model name")
                .or_else(|| line.strip_prefix("Hardware"))
        })
        .and_then(|rest| rest.split_once(':'))
        .map(|(_, v)| v.trim().to_string())
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "Linux CPU".into());
    let vendor = if brand.to_ascii_lowercase().contains("intel") {
        "Intel"
    } else if brand.to_ascii_lowercase().contains("amd") {
        "AMD"
    } else {
        "unknown"
    };
    (vendor.into(), brand)
}

#[cfg(target_os = "macos")]
fn sysctl_brand() -> Option<String> {
    let out = std::process::Command::new("sysctl")
        .args(["-n", "machdep.cpu.brand_string"])
        .output()
        .ok()?;
    if !out.status.success() {
        return None;
    }
    let s = String::from_utf8_lossy(&out.stdout).trim().to_string();
    if s.is_empty() {
        None
    } else {
        Some(s)
    }
}

fn vendor_from_pci(id: u32) -> &'static str {
    match id {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        _ => "other",
    }
}

#[cfg(windows)]
mod dxgi {
    use super::{vendor_from_pci, GpuInfo};
    use std::ffi::c_void;
    use std::ptr;

    #[repr(C)]
    struct Guid {
        data1: u32,
        data2: u16,
        data3: u16,
        data4: [u8; 8],
    }

    // IID_IDXGIFactory1 {770aae78-f26f-4dba-a829-253c83d1b387}
    const IID_IDXGI_FACTORY1: Guid = Guid {
        data1: 0x770a_ae78,
        data2: 0xf26f,
        data3: 0x4dba,
        data4: [0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87],
    };

    #[repr(C)]
    struct Luid {
        low: u32,
        high: i32,
    }

    #[repr(C)]
    struct DxgiAdapterDesc1 {
        description: [u16; 128],
        vendor_id: u32,
        device_id: u32,
        sub_sys_id: u32,
        revision: u32,
        dedicated_video_memory: usize,
        dedicated_system_memory: usize,
        shared_system_memory: usize,
        adapter_luid: Luid,
        flags: u32,
    }

    const DXGI_ADAPTER_FLAG_SOFTWARE: u32 = 2;

    #[link(name = "dxgi")]
    extern "system" {
        fn CreateDXGIFactory1(riid: *const Guid, factory: *mut *mut c_void) -> i32;
    }

    unsafe fn vtable(obj: *mut c_void) -> *const *const c_void {
        *(obj as *const *const *const c_void)
    }

    unsafe fn release(obj: *mut c_void) {
        if obj.is_null() {
            return;
        }
        let vt = vtable(obj);
        let f: unsafe extern "system" fn(*mut c_void) -> u32 = std::mem::transmute(*vt.add(2));
        let _ = f(obj);
    }

    pub fn enumerate() -> Vec<GpuInfo> {
        unsafe {
            let mut factory: *mut c_void = ptr::null_mut();
            let hr = CreateDXGIFactory1(&IID_IDXGI_FACTORY1, &mut factory);
            if hr < 0 || factory.is_null() {
                return Vec::new();
            }
            let mut out = Vec::new();
            let vt = vtable(factory);
            let enum_adapters1: unsafe extern "system" fn(
                *mut c_void,
                u32,
                *mut *mut c_void,
            ) -> i32 = std::mem::transmute(*vt.add(12));
            let mut i = 0u32;
            loop {
                let mut adapter: *mut c_void = ptr::null_mut();
                let hr = enum_adapters1(factory, i, &mut adapter);
                if hr < 0 || adapter.is_null() {
                    break;
                }
                let avt = vtable(adapter);
                let get_desc1: unsafe extern "system" fn(
                    *mut c_void,
                    *mut DxgiAdapterDesc1,
                ) -> i32 = std::mem::transmute(*avt.add(10));
                let mut desc = std::mem::zeroed::<DxgiAdapterDesc1>();
                if get_desc1(adapter, &mut desc) >= 0
                    && desc.flags & DXGI_ADAPTER_FLAG_SOFTWARE == 0
                {
                    let end = desc
                        .description
                        .iter()
                        .position(|&c| c == 0)
                        .unwrap_or(desc.description.len());
                    let name = String::from_utf16_lossy(&desc.description[..end]);
                    if !name.contains("Microsoft Basic Render") {
                        out.push(GpuInfo {
                            vendor: vendor_from_pci(desc.vendor_id).to_string(),
                            name,
                            vram_mb: (desc.dedicated_video_memory as u64) / (1024 * 1024),
                            index: out.len() as u32,
                            driver: String::new(),
                        });
                    }
                }
                release(adapter);
                i += 1;
                if i > 32 {
                    break;
                }
            }
            release(factory);
            out
        }
    }
}

fn env_gpu_fallback() -> Vec<GpuInfo> {
    let mut gpus = Vec::new();
    if env::var("CUDA_PATH").is_ok() || env::var("CUDA_VISIBLE_DEVICES").is_ok() {
        gpus.push(GpuInfo {
            vendor: "NVIDIA".into(),
            name: env::var("CCP_GPU_NAME").unwrap_or_else(|_| "NVIDIA (env)".into()),
            vram_mb: 0,
            index: 0,
            driver: String::new(),
        });
    }
    gpus
}

fn enumerate_gpus() -> Vec<GpuInfo> {
    #[cfg(windows)]
    {
        let gpus = dxgi::enumerate();
        if !gpus.is_empty() {
            return gpus;
        }
    }
    #[cfg(target_os = "linux")]
    {
        let gpus = nvidia_smi_gpus();
        if !gpus.is_empty() {
            return gpus;
        }
    }
    env_gpu_fallback()
}

#[cfg(target_os = "linux")]
fn nvidia_smi_gpus() -> Vec<GpuInfo> {
    let out = match std::process::Command::new("nvidia-smi")
        .args([
            "--query-gpu=name,memory.total",
            "--format=csv,noheader,nounits",
        ])
        .output()
    {
        Ok(o) if o.status.success() => o,
        _ => return Vec::new(),
    };
    let mut gpus = Vec::new();
    for (index, line) in String::from_utf8_lossy(&out.stdout).lines().enumerate() {
        let mut parts = line.split(',').map(str::trim);
        let name = parts.next().unwrap_or("").to_string();
        if name.is_empty() {
            continue;
        }
        let vram_mb = parts
            .next()
            .and_then(|s| s.parse::<u64>().ok())
            .unwrap_or(0);
        gpus.push(GpuInfo {
            vendor: "NVIDIA".into(),
            name,
            vram_mb,
            index: index as u32,
            driver: String::new(),
        });
    }
    gpus
}

fn tch_cuda_available() -> bool {
    #[cfg(feature = "tch")]
    {
        return tch::Cuda::is_available();
    }
    #[allow(unreachable_code)]
    false
}

fn tch_mps_available() -> bool {
    cfg!(all(feature = "tch", target_os = "macos"))
}

fn is_apple_silicon() -> bool {
    cfg!(all(target_os = "macos", target_arch = "aarch64"))
}

fn cuda_usable(json: Option<&LibTorchJson>, gpus: &[GpuInfo]) -> bool {
    if tch_cuda_available() {
        return true;
    }
    let nvidia = gpus.iter().any(|g| g.vendor.eq_ignore_ascii_case("NVIDIA"));
    let kind_cuda = json
        .map(|j| j.kind.eq_ignore_ascii_case("cuda"))
        .unwrap_or(false);
    kind_cuda && nvidia && (env::var("CUDA_PATH").is_ok() || env::var("CCP_CUDA").is_ok())
}

fn dll_exists(name: &str) -> bool {
    let mut dirs = Vec::new();
    if let Ok(exe) = env::current_exe() {
        if let Some(dir) = exe.parent() {
            dirs.push(dir.to_path_buf());
        }
    }
    if let Ok(root) = env::var("CCP_NEW_UI_ROOT") {
        dirs.push(PathBuf::from(root));
    }
    #[cfg(windows)]
    {
        let sys = env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".into());
        dirs.push(PathBuf::from(sys).join("System32"));
    }
    if let Ok(path) = env::var("PATH") {
        for part in env::split_paths(&path).take(24) {
            dirs.push(part);
        }
    }
    dirs.iter().any(|d| d.join(name).is_file())
}

fn ort_ep_available(name: &str) -> (bool, Option<String>) {
    #[cfg(feature = "ort-plane")]
    {
        use ort::ep::ExecutionProvider;
        let check = || -> Result<bool, String> {
            match name {
                "CUDAExecutionProvider" => ort::ep::CUDA::default()
                    .is_available()
                    .map_err(|e| e.to_string()),
                "DmlExecutionProvider" => ort::ep::DirectML::default()
                    .is_available()
                    .map_err(|e| e.to_string()),
                "OpenVINOExecutionProvider" => ort::ep::OpenVINO::default()
                    .is_available()
                    .map_err(|e| e.to_string()),
                "CoreMLExecutionProvider" => ort::ep::CoreML::default()
                    .is_available()
                    .map_err(|e| e.to_string()),
                _ => Ok(false),
            }
        };
        match check() {
            Ok(true) => (true, None),
            Ok(false) => (false, Some(format!("{name}_not_in_runtime"))),
            Err(e) => (false, Some(e)),
        }
    }
    #[cfg(not(feature = "ort-plane"))]
    {
        let _ = name;
        (false, Some("ort_not_linked".into()))
    }
}

#[derive(Clone, Copy)]
struct Caps {
    cuda_torch: bool,
    mps_torch: bool,
    mlx_hw: bool,
    ort_cuda: bool,
    ort_dml: bool,
    ort_ov: bool,
    ort_coreml: bool,
    intel: bool,
}

fn first_used(assignment: &ComputeAssignment, names: &[&str]) -> &'static str {
    for stage in [
        assignment.detect.as_str(),
        assignment.plane.as_str(),
        assignment.apply.as_str(),
    ] {
        if names.contains(&stage) {
            return "active";
        }
    }
    "unused"
}

fn used_for(assignment: &ComputeAssignment, names: &[&str]) -> String {
    let mut parts = Vec::new();
    if names.contains(&assignment.detect.as_str()) {
        parts.push("detect");
    }
    if names.contains(&assignment.plane.as_str()) {
        parts.push("plane");
    }
    if names.contains(&assignment.apply.as_str()) {
        parts.push("apply");
    }
    if parts.is_empty() {
        "unused".into()
    } else {
        parts.join(",")
    }
}

fn pick_detect(policy: DevicePolicy, caps: Caps) -> String {
    match policy {
        DevicePolicy::Cpu => "cpu".into(),
        DevicePolicy::Cuda => {
            if caps.cuda_torch {
                "cuda".into()
            } else if caps.ort_dml {
                "directml".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::Mlx => {
            if caps.mps_torch {
                "mps".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::DirectMl => {
            if caps.ort_dml {
                "directml".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::OpenVino => {
            if caps.ort_ov {
                "openvino".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::Auto => {
            if caps.cuda_torch {
                "cuda".into()
            } else if caps.mps_torch {
                "mps".into()
            } else if caps.ort_dml {
                "directml".into()
            } else if caps.ort_ov {
                "openvino".into()
            } else {
                "cpu".into()
            }
        }
    }
}

fn pick_plane(policy: DevicePolicy, caps: Caps) -> String {
    match policy {
        DevicePolicy::Cpu => "cpu".into(),
        DevicePolicy::Cuda => {
            if caps.ort_cuda {
                "cuda".into()
            } else if caps.ort_dml {
                "directml".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::DirectMl => {
            if caps.ort_dml {
                "directml".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::OpenVino => {
            if caps.ort_ov {
                "openvino".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::Mlx => {
            if caps.ort_coreml {
                "coreml".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::Auto => {
            if caps.ort_cuda {
                "cuda".into()
            } else if caps.intel && caps.ort_ov {
                "openvino".into()
            } else if caps.ort_dml {
                "directml".into()
            } else if caps.ort_ov {
                "openvino".into()
            } else if caps.ort_coreml {
                "coreml".into()
            } else {
                "cpu".into()
            }
        }
    }
}

fn pick_apply(policy: DevicePolicy, caps: Caps) -> String {
    match policy {
        DevicePolicy::Cpu => "cpu".into(),
        DevicePolicy::Cuda => {
            if caps.cuda_torch {
                "cuda".into()
            } else if caps.ort_dml {
                "directml".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::Mlx => {
            if caps.mps_torch {
                "mps".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::DirectMl => {
            if caps.ort_dml {
                "directml".into()
            } else {
                "cpu".into()
            }
        }
        DevicePolicy::OpenVino => {
            if caps.ort_ov {
                "openvino".into()
            } else {
                "cpu".into()
            }
        }
        // A 33³ still-image LUT is tiny and CPU sampling avoids host↔device
        // transfer. Accelerators remain available through explicit policies.
        DevicePolicy::Auto => "cpu".into(),
    }
}

fn short_stage(name: &str) -> &'static str {
    match name {
        "cuda" => "CUDA",
        "directml" => "DML",
        "openvino" => "OpenVINO",
        "mps" => "MPS",
        "coreml" => "CoreML",
        "mlx" => "MLX",
        _ => "CPU",
    }
}

fn recommended_label(assignment: &ComputeAssignment, gpus: &[GpuInfo], cpu: &CpuInfo) -> String {
    let mut seen = Vec::new();
    for stage in [
        assignment.detect.as_str(),
        assignment.plane.as_str(),
        assignment.apply.as_str(),
    ] {
        if stage != "cpu" && !seen.contains(&stage) {
            seen.push(stage);
        }
    }
    if seen.is_empty() {
        return format!("CPU · {} · {} thr", cpu.simd, assignment.threads);
    }
    let stack = seen
        .iter()
        .map(|s| short_stage(s))
        .collect::<Vec<_>>()
        .join("+");
    let gpu_name = if seen.contains(&"cuda") {
        gpus.iter()
            .find(|g| g.vendor.eq_ignore_ascii_case("NVIDIA"))
            .map(|g| g.name.as_str())
    } else if seen.contains(&"openvino") {
        gpus.iter()
            .find(|g| g.vendor.eq_ignore_ascii_case("Intel"))
            .map(|g| g.name.as_str())
    } else if seen.contains(&"directml") {
        gpus.iter()
            .find(|g| {
                g.vendor.eq_ignore_ascii_case("AMD") || g.vendor.eq_ignore_ascii_case("Intel")
            })
            .or_else(|| gpus.first())
            .map(|g| g.name.as_str())
    } else if seen.iter().any(|s| matches!(*s, "mps" | "coreml" | "mlx")) {
        gpus.iter()
            .find(|g| g.vendor.eq_ignore_ascii_case("Apple"))
            .map(|g| g.name.as_str())
            .or(Some("Apple GPU"))
    } else {
        None
    };
    match gpu_name {
        Some(name) if !name.is_empty() => format!("{stack} · {name}"),
        _ => stack,
    }
}

fn headline(assignment: &ComputeAssignment, caps: Caps) -> BackendKind {
    match assignment.detect.as_str() {
        "cuda" => BackendKind::LibTorchCuda,
        "mps" => BackendKind::LibTorchMps,
        _ => match assignment.plane.as_str() {
            "cuda" => BackendKind::OrtCuda,
            "directml" => BackendKind::OrtDirectMl,
            "openvino" => BackendKind::OrtOpenVino,
            "coreml" => BackendKind::OrtCoreMl,
            _ if caps.mlx_hw && assignment.apply == "mps" => BackendKind::LibTorchMps,
            _ => BackendKind::LibTorchCpu,
        },
    }
}

fn slot(kind: BackendKind, status: &str, used_for: String, fail: Option<String>) -> BackendInfo {
    BackendInfo {
        kind,
        status: status.into(),
        device_index: 0,
        used_for,
        fail_reason: fail,
    }
}

/// Probe CPU, GPUs, LibTorch, and ONNX Runtime execution providers. Assign detect / plane / apply.
pub fn probe_accelerators(policy: &str) -> AcceleratorReport {
    let policy = DevicePolicy::parse(policy);
    let cpu = cpu_info();
    let mut gpus = enumerate_gpus();
    if is_apple_silicon() && !gpus.iter().any(|g| g.vendor.eq_ignore_ascii_case("Apple")) {
        gpus.push(GpuInfo {
            vendor: "Apple".into(),
            name: "Apple GPU".into(),
            vram_mb: 0,
            index: gpus.len() as u32,
            driver: "metal".into(),
        });
    }
    let root = default_libtorch_root();
    let json = read_libtorch_json(&root);
    let version_ok = json
        .as_ref()
        .map(|j| libtorch_version_matches_29(&j.version))
        .unwrap_or(false);
    let nvidia = gpus.iter().any(|g| g.vendor.eq_ignore_ascii_case("NVIDIA"));
    let amd = gpus.iter().any(|g| g.vendor.eq_ignore_ascii_case("AMD"));
    let intel = gpus.iter().any(|g| g.vendor.eq_ignore_ascii_case("Intel"));
    let cuda_torch = version_ok && cuda_usable(json.as_ref(), &gpus);
    let mps_torch = tch_mps_available();
    let mlx_hw = is_apple_silicon();
    let dml_dll = cfg!(windows) && (dll_exists("DirectML.dll") || amd || intel || nvidia);
    let (ort_cuda_api, ort_cuda_fail) = if nvidia {
        ort_ep_available("CUDAExecutionProvider")
    } else {
        (false, Some("no_nvidia_gpu".into()))
    };
    let (ort_dml_api, ort_dml_fail) = if cfg!(windows) {
        ort_ep_available("DmlExecutionProvider")
    } else {
        (false, Some("windows_only".into()))
    };
    let (ort_ov_api, ort_ov_fail) = ort_ep_available("OpenVINOExecutionProvider");
    let (ort_coreml_api, ort_coreml_fail) = if cfg!(target_os = "macos") {
        ort_ep_available("CoreMLExecutionProvider")
    } else {
        (false, Some("macos_only".into()))
    };

    // Usable = ORT says the EP exists, or we can reasonably try it (DLL + GPU).
    // ORT may advertise an EP compiled into its API even when the provider
    // library was not packaged beside the executable. LibTorch CUDA does not
    // imply ONNX Runtime CUDA is usable.
    let ort_cuda_library = !cfg!(windows) || dll_exists("onnxruntime_providers_cuda.dll");
    let ort_openvino_library = !cfg!(windows) || dll_exists("onnxruntime_providers_openvino.dll");
    let ort_cuda = ort_cuda_api && ort_cuda_library;
    let ort_dml = ort_dml_api || dml_dll;
    let ort_ov = ort_ov_api && ort_openvino_library;
    let ort_coreml = ort_coreml_api || cfg!(all(feature = "ort-plane", target_os = "macos"));

    let caps = Caps {
        cuda_torch,
        mps_torch,
        mlx_hw,
        ort_cuda,
        ort_dml,
        ort_ov,
        ort_coreml,
        intel,
    };
    let assignment = ComputeAssignment {
        detect: pick_detect(policy, caps),
        plane: pick_plane(policy, caps),
        apply: pick_apply(policy, caps),
        threads: apply_thread_count(&cpu),
    };
    let recommended = recommended_label(&assignment, &gpus, &cpu);
    let active = headline(&assignment, caps);

    let mut cuda_fail: Option<String> = None;
    if json.as_ref().is_some() && !version_ok {
        cuda_fail = Some("version_mismatch".into());
    } else if json
        .as_ref()
        .map(|j| j.kind.eq_ignore_ascii_case("cuda"))
        .unwrap_or(false)
        && !cuda_torch
    {
        cuda_fail = Some("no_runtime".into());
    }

    let torch_cpu_used = used_for(&assignment, &["cpu"]);
    let torch_cuda_used = {
        let mut p = Vec::new();
        if assignment.detect == "cuda" {
            p.push("detect");
        }
        if assignment.apply == "cuda" {
            p.push("apply");
        }
        if p.is_empty() {
            "unused".into()
        } else {
            p.join(",")
        }
    };
    let torch_mps_used = {
        let mut p = Vec::new();
        if assignment.detect == "mps" {
            p.push("detect");
        }
        if assignment.apply == "mps" {
            p.push("apply");
        }
        if p.is_empty() {
            "unused".into()
        } else {
            p.join(",")
        }
    };
    let dml_used = used_for(&assignment, &["directml"]);
    let ov_used = used_for(&assignment, &["openvino"]);
    let coreml_used = if assignment.plane == "coreml" {
        "plane".into()
    } else {
        "unused".into()
    };

    let backends = vec![
        slot(
            BackendKind::LibTorchCpu,
            if torch_cpu_used != "unused" {
                "active"
            } else {
                "available"
            },
            torch_cpu_used,
            if !version_ok && json.is_some() {
                Some("version_mismatch".into())
            } else {
                None
            },
        ),
        slot(
            BackendKind::LibTorchCuda,
            if first_used(&assignment, &["cuda"]) == "active" && cuda_torch {
                "active"
            } else if cuda_torch {
                "available"
            } else if nvidia {
                "failed"
            } else {
                "unavailable"
            },
            torch_cuda_used,
            cuda_fail,
        ),
        slot(
            BackendKind::OrtCuda,
            if assignment.plane == "cuda" {
                "active"
            } else if ort_cuda {
                "available"
            } else if nvidia {
                "detected"
            } else {
                "unavailable"
            },
            if assignment.plane == "cuda" {
                "plane".into()
            } else {
                "unused".into()
            },
            if ort_cuda { None } else { ort_cuda_fail },
        ),
        slot(
            BackendKind::OrtDirectMl,
            if first_used(&assignment, &["directml"]) == "active" {
                "active"
            } else if ort_dml {
                "available"
            } else if cfg!(windows) && (amd || intel || nvidia) {
                "detected"
            } else {
                "unavailable"
            },
            dml_used,
            if ort_dml {
                None
            } else {
                ort_dml_fail.or_else(|| {
                    if !cfg!(windows) {
                        Some("windows_only".into())
                    } else {
                        None
                    }
                })
            },
        ),
        slot(
            BackendKind::OrtOpenVino,
            if first_used(&assignment, &["openvino"]) == "active" {
                "active"
            } else if ort_ov {
                "available"
            } else if intel {
                "detected"
            } else {
                "unavailable"
            },
            ov_used,
            if ort_ov { None } else { ort_ov_fail },
        ),
        slot(
            BackendKind::LibTorchMps,
            if assignment.detect == "mps" || assignment.apply == "mps" {
                "active"
            } else if mps_torch {
                "available"
            } else if cfg!(target_os = "macos") {
                "detected"
            } else {
                "unavailable"
            },
            torch_mps_used,
            if mps_torch {
                None
            } else if !cfg!(target_os = "macos") {
                Some("macos_only".into())
            } else {
                Some("libtorch_mps_not_linked".into())
            },
        ),
        slot(
            BackendKind::OrtCoreMl,
            if assignment.plane == "coreml" {
                "active"
            } else if ort_coreml {
                "available"
            } else if cfg!(target_os = "macos") {
                "detected"
            } else {
                "unavailable"
            },
            coreml_used,
            if ort_coreml { None } else { ort_coreml_fail },
        ),
        slot(
            BackendKind::Mlx,
            if mlx_hw { "detected" } else { "unavailable" },
            "unused".into(),
            if mlx_hw {
                Some("mlx_runtime_not_linked".into())
            } else {
                Some("apple_silicon_only".into())
            },
        ),
    ];
    AcceleratorReport {
        cpu,
        gpus,
        backends,
        active,
        libtorch: LibTorchInfo {
            kind: json
                .as_ref()
                .map(|j| j.kind.clone())
                .unwrap_or_else(|| "unknown".into()),
            version: json.as_ref().map(|j| j.version.clone()).unwrap_or_default(),
            root: root.to_string_lossy().into_owned(),
        },
        policy: policy.as_str().to_string(),
        assignment,
        recommended,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn caps(cuda_torch: bool, mps_torch: bool, ort_dml: bool, ort_ov: bool, intel: bool) -> Caps {
        Caps {
            cuda_torch,
            mps_torch,
            mlx_hw: mps_torch,
            ort_cuda: cuda_torch,
            ort_dml,
            ort_ov,
            ort_coreml: false,
            intel,
        }
    }

    #[test]
    fn pick_detect_auto_prefers_cuda_torch() {
        let c = caps(true, false, true, false, false);
        assert_eq!(pick_detect(DevicePolicy::Auto, c), "cuda");
    }

    #[test]
    fn pick_detect_auto_falls_back_to_dml() {
        let c = caps(false, false, true, false, false);
        assert_eq!(pick_detect(DevicePolicy::Auto, c), "directml");
    }

    #[test]
    fn pick_detect_directml_policy() {
        let c = caps(true, false, true, false, false);
        assert_eq!(pick_detect(DevicePolicy::DirectMl, c), "directml");
    }

    #[test]
    fn auto_apply_prefers_cpu_for_small_lut() {
        let c = caps(false, false, true, false, false);
        assert_eq!(pick_apply(DevicePolicy::Auto, c), "cpu");
        assert_eq!(pick_plane(DevicePolicy::Auto, c), "directml");
    }

    #[test]
    fn pick_detect_cpu_policy() {
        let c = caps(true, true, true, false, false);
        assert_eq!(pick_detect(DevicePolicy::Cpu, c), "cpu");
    }
}
