# Compatibility and migration

The current app keeps the Avalonia interface, Rust correction engine, built-in reference tables, legacy model/session readers and the `CCP-New` data location.

- Fast correction is the default: a combined 65³ GC/WB/CC LUT. Advanced settings retain the previous separate-stage method for reproducing older outputs. FFC remains spatial and separate.
- CPU/GPU selects correction execution. Detection uses DirectML or WebGPU/Vulkan and fails clearly if the requested backend cannot run. There is no CPU inference fallback.
- Torch/LibTorch, CUDA add-on installation and video processing have been removed. Old video operations return an unsupported-operation error.
- Custom reference snapshots and chart assignments travel with sessions and fitted-model provenance. Redetection invalidates custom assignments. Models apply without the original CSV/JSON; FFC still needs its blank-field asset.
- The familiar Python `ColorCorrection` workflow and typed `Client` API coexist with legacy `Engine` imports and raw replies. File paths replace array inputs; typed results expose actual artifact paths and per-image failures. [API differences](../api/README.md)
- Embedded ICC profiles are honored for sRGB/D65 input. Refit models trained on previously misinterpreted tagged images. Explicit non-sRGB input encoding remains the caller's responsibility.

Detector weights and built-in chart values are unchanged. See
[model provenance and licensing](model-provenance.md) for model identities and terms.
