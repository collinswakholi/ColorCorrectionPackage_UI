# Compatibility and migration

The current app keeps the Avalonia interface, Rust correction engine, built-in reference tables, readers for models and sessions saved by Studio 0.1 and later, and the `CCP-New` data location. Models saved before Studio 0.1 must be refit.

- Fast correction is the default: a combined 65³ GC/WB/CC LUT, applied in the same pass as FFC since 0.5.0. Fused output matches 0.4.0 to within 1/255. Advanced settings keep the previous separate-stage method for reproducing older outputs.
- CPU/GPU selects correction execution. Auto detection tries eligible DirectML/WebGPU routes, then explicit CPU inference. Forced GPU detection fails clearly when unavailable. The `cpu-inference` policy selects CPU detection; `cpu` selects CPU correction with Auto detection.
- Torch/LibTorch, CUDA add-on installation and video processing have been removed. Old video operations return an unsupported-operation error.
- Custom reference snapshots and chart assignments travel with sessions and fitted-model provenance. Redetection invalidates custom assignments. Models apply without the original CSV/JSON; FFC still needs its blank-field asset.
- The familiar Python `ColorCorrection` workflow and the typed `Client` API coexist with the low-level `Engine` API and raw replies. File paths replace array inputs; typed results expose actual artifact paths and per-image failures. [API differences](../api/README.md)
- Embedded ICC profiles are honored for sRGB/D65 input. Refit models trained on previously misinterpreted tagged images. Explicit non-sRGB input encoding remains the caller's responsibility.

Detector weights and built-in chart values are unchanged. See
[model provenance and licensing](model-provenance.md) for model identities and terms.
