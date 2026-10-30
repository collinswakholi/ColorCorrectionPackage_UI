//! Hardware inventory and portable GPU inference policy.

use std::env;
use std::sync::Mutex;
use std::thread::available_parallelism;

use serde::{Deserialize, Serialize};


#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BackendKind { OrtDirectMl, OrtWebGpu, OrtCpu }
impl BackendKind {
    pub fn as_str(self) -> &'static str { match self { Self::OrtDirectMl => "OrtDirectMl", Self::OrtWebGpu => "OrtWebGpu", Self::OrtCpu => "OrtCpu" } }
    pub fn short(self) -> &'static str { match self { Self::OrtDirectMl => "DirectML", Self::OrtWebGpu => "WebGPU", Self::OrtCpu => "CPU" } }
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
pub struct AcceleratorReport {
    pub cpu: CpuInfo,
    pub gpus: Vec<GpuInfo>,
    pub backends: Vec<BackendInfo>,
    pub active: BackendKind,
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
pub enum DevicePolicy { Auto, Cpu, CpuInference, DirectMl, WebGpu, Vulkan }
impl DevicePolicy {
    pub fn parse(s: &str) -> Self { match s.trim().to_ascii_lowercase().as_str() {
        "cpu" => Self::Cpu, "cpu-inference" => Self::CpuInference, "directml" | "dml" => Self::DirectMl,
        "webgpu" => Self::WebGpu, "vulkan" => Self::Vulkan, _ => Self::Auto,
    } }
    pub fn as_str(self) -> &'static str { match self { Self::Auto => "auto", Self::Cpu => "cpu", Self::CpuInference => "cpu-inference", Self::DirectMl => "directml", Self::WebGpu => "webgpu", Self::Vulkan => "vulkan" } }
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
                    if !super::software_renderer(&name, false, false) {
                        out.push(GpuInfo {
                            vendor: vendor_from_pci(desc.vendor_id).to_string(),
                            name,
                            vram_mb: (desc.dedicated_video_memory as u64) / (1024 * 1024),
                            index: i,
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

pub(crate) fn enumerate_gpus() -> Vec<GpuInfo> {
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
    Vec::new()
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

/// Per-user setup is shared by the desktop app and installed SDK workers.
pub fn selected_components() -> String {
    if let Ok(value)=std::env::var("CCP_INFERENCE_COMPONENTS") {return value;}
    let user=if cfg!(windows) {
        std::env::var_os("LOCALAPPDATA").map(|p|std::path::PathBuf::from(p).join("ColorCorrectStudio/setup-state.json"))
    } else {
        std::env::var_os("XDG_CONFIG_HOME").map(std::path::PathBuf::from)
            .or_else(||std::env::var_os("HOME").map(|p|std::path::PathBuf::from(p).join(".config")))
            .map(|p|p.join("colorcorrect/setup-state.json"))
    };
    let portable=std::env::current_exe().ok().and_then(|exe|exe.parent().map(|p|p.join("setup-state.json")));
    for path in [user,portable].into_iter().flatten() {
        if let Some(value)=std::fs::read_to_string(path).ok().and_then(|text|serde_json::from_str::<serde_json::Value>(&text).ok()) {
            if let Some(items)=value.get("components").and_then(|c|c.as_array()) {
                return items.iter().filter_map(|item|item.as_str()).collect::<Vec<_>>().join(",");
            }
        }
    }
    "cpu,directml,webgpu".into()
}
pub fn gpu_correction_enabled() -> bool {
    selected_components().split(',').any(|part|matches!(part.trim(),"webgpu"|"directml"))
}

/// Shared software-renderer rejection. Hardware-backed virtual adapters remain eligible.
pub fn software_renderer(name: &str, cpu_device: bool, dxgi_software: bool) -> bool {
    let name = name.to_ascii_lowercase();
    cpu_device || dxgi_software || ["llvmpipe", "lavapipe", "swiftshader", "microsoft basic render"]
        .iter().any(|software| name.contains(software))
}

#[cfg(any(feature="ort-plane",feature="wgpu-lut"))]
pub(crate) fn eligible_wgpu_adapter(info: &wgpu::AdapterInfo) -> bool {
    !software_renderer(&info.name, info.device_type == wgpu::DeviceType::Cpu, false)
        && matches!(info.device_type, wgpu::DeviceType::DiscreteGpu | wgpu::DeviceType::IntegratedGpu | wgpu::DeviceType::VirtualGpu)
}

/// GPU provider selection is a plan, not proof of model execution.
pub fn probe_accelerators(policy: &str) -> AcceleratorReport {
    let policy = DevicePolicy::parse(policy);
    let cpu = cpu_info();
    let gpus = if policy==DevicePolicy::CpuInference || !gpu_correction_enabled() {Vec::new()} else {enumerate_gpus()};
    let detect = match policy {
        DevicePolicy::CpuInference => "cpu",
        DevicePolicy::DirectMl => "directml",
        DevicePolicy::WebGpu => "webgpu",
        DevicePolicy::Vulkan => "vulkan",
        _ if !gpu_correction_enabled() => "cpu",
        _ if cfg!(windows) => "directml",
        _ => "webgpu",
    };
    let assignment = ComputeAssignment {
        detect: detect.into(), plane: detect.into(),
        apply: if policy == DevicePolicy::Cpu || !gpu_correction_enabled() || !cfg!(feature = "wgpu-lut") { "cpu".into() } else { "wgpu".into() },
        threads: cpu.cores_physical.max(1).min(cpu.cores_logical.max(1)).min(8),
    };
    let active = if detect == "cpu" { BackendKind::OrtCpu } else if detect == "directml" { BackendKind::OrtDirectMl } else { BackendKind::OrtWebGpu };
    let backends = [BackendKind::OrtDirectMl, BackendKind::OrtWebGpu, BackendKind::OrtCpu].into_iter().map(|kind| BackendInfo {
        kind, status: if kind == BackendKind::OrtDirectMl && !cfg!(windows) { "unavailable" }
            else if kind == active { "planned" } else { "not tested" }.into(),
        device_index: -1, used_for: if kind == active { "detect,plane" } else { "unused" }.into(), fail_reason: None,
    }).collect();
    AcceleratorReport { cpu, gpus, backends, active, policy: policy.as_str().into(),
        recommended: format!("{} inference (not checked) · {} LUT", if matches!(policy,DevicePolicy::Auto|DevicePolicy::Cpu) {"Auto"} else {active.short()}, assignment.apply), assignment }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn software_renderer_filter_does_not_reject_vendors_or_virtual_hardware() {
        for name in ["Microsoft Basic Render Driver","llvmpipe LLVM","lavapipe","Google SwiftShader"] {
            assert!(software_renderer(name,false,false));
        }
        for name in ["Microsoft Direct3D12 (NVIDIA)","Mesa Intel Graphics","Apple Paravirtual GPU","AMD Radeon","Adreno","Mali"] {
            assert!(!software_renderer(name,false,false));
        }
        assert!(software_renderer("Intel",false,true));
        assert!(software_renderer("Unknown",true,false));
    }
    #[test]
    fn explicit_cpu_inference_is_distinct_from_legacy_cpu_correction() {
        let report=probe_accelerators("cpu-inference");
        assert_eq!(report.assignment.detect,"cpu");assert_eq!(report.assignment.plane,"cpu");
        assert_eq!(report.policy,"cpu-inference");
    }
    #[test]
    fn cpu_policy_only_controls_lut() {
        let auto = probe_accelerators("auto");
        let report = probe_accelerators("cpu");
        assert_eq!(report.assignment.apply, "cpu");
        assert_eq!(report.policy, "cpu");
        assert_eq!(report.assignment.detect, auto.assignment.detect);
        assert_eq!(report.assignment.plane, auto.assignment.plane);
    }
}
