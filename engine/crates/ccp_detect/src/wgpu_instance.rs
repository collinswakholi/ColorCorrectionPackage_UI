//! One wgpu instance for the whole process.
//!
//! Dropping a wgpu instance makes the Vulkan loader unload its drivers. Some
//! drivers register per-thread destructors (Mesa 23.x lavapipe/LLVM on Ubuntu
//! 22.04 does), so a thread that had used Vulkan and exits after the unload
//! jumps into unmapped code and the process dies with SIGSEGV. This happened
//! whenever the GPU probe found no hardware adapter and its instance was
//! dropped. Keeping one instance alive keeps the drivers loaded, and also
//! avoids re-creating a Vulkan/DX12 instance (hundreds of milliseconds) per probe.
use std::sync::OnceLock;

static INSTANCE: OnceLock<wgpu::Instance> = OnceLock::new();

/// The shared instance (DX12, Vulkan and Metal; WGPU_* environment opt-ins honoured).
/// Callers choose the backends to search when they enumerate adapters.
pub fn shared() -> &'static wgpu::Instance {
    INSTANCE.get_or_init(|| {
        let mut descriptor = wgpu::InstanceDescriptor::new_without_display_handle();
        descriptor.backends = wgpu::Backends::DX12 | wgpu::Backends::VULKAN | wgpu::Backends::METAL;
        // Explicit WGPU_* opt-ins help non-conformant VM translation drivers;
        // callers still reject CPU/software adapters.
        descriptor.flags = descriptor.flags.with_env();
        wgpu::Instance::new(descriptor)
    })
}
