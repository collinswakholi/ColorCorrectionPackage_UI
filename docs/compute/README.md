# Portable compute: current behavior

Production inference uses DirectML or WebGPU/Vulkan. LUT correction uses Rust CPU or wgpu with transactional CPU recovery. Torch, LibTorch, CUDA add-ons and video processing have been removed.

**Auto, CPU and GPU** select the correction device; all default to the combined **65³ GC/WB/CC LUT**. Auto uses CPU below 1,048,576 pixels and attempts GPU above that threshold. This conservative policy is not an adaptive performance guarantee. Detection remains GPU-based even when correction is set to CPU.

The previous method in Advanced evaluates GC/WB separately and uses a 33³ CC LUT. It is retained for old-result reproduction and comparison, not as an exact or universally more accurate mode. FFC remains a separate spatial operation. Extended-domain inputs use the established safe path automatically.

## Backend validation

Windows Auto uses DirectML, which was faster on the tested NVIDIA hardware than native WebGPU/D3D12 or Vulkan. Windows Vulkan loader initialization now searches the system loader correctly. WSL uses a Rust/wgpu eager model route because the tested Dawn/Dozen combination lacks a required drawing feature. Fusion/autotuning are disabled for that route after photographic correctness failures were found.

The accepted WSL experiment matched canonical cells within 0.0001 model pixel over 36 reordered-photo detections and passed installed application/API workflows. The configured Dozen translation driver and hardware are part of that evidence. This is not a universal Linux performance claim. Missing libraries/adapters/operators produce actionable inference errors; they do not enable CPU inference.

## Measurements and limitations

Earlier 24 MP combined-LUT measurements were about 0.737 s on GPU and 0.764 s on CPU, versus 1.628 s for the previous GPU processing method. These are recorded measurements on one machine, not promises for other drivers or all algorithms. Identical LUT arithmetic matched within 5.96e-8 normalized RGB; LUT approximation was assessed separately.

Cold initialization, data transfers, image encoding, FFC and fitting can dominate total time even when GPU kernels are faster. Reports preserve actual providers, completed stages and mixed execution. Ordinary UI logs use shorter labels.

Raw benchmarks, rejected trials, exact commands and old Torch comparisons remain privately preserved in ignored local evidence/research folders. They are not active build instructions. The current readiness run must pin fresh source/package/model hashes and record its own outcome. [Acceptance gates](../native-validation.md)
