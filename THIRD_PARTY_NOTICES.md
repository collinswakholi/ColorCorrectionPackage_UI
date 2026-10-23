# Third-party software and data

The repository's MIT license covers this project's source, not every bundled
component. Runtime archives and SDK dependencies retain their upstream terms.
The following lists principal components. A staged local validation package is
not a published release; complete dependency notices must accompany any public
redistribution. Do not infer missing notices from this inventory.

| Component | Version / terms | Source/access |
| --- | --- | --- |
| Avalonia | 12.0.2, MIT | https://github.com/AvaloniaUI/Avalonia |
| .NET | SDK 10.0.402; runtime 10.0.13, MIT and third-party notices | https://github.com/dotnet/runtime/tree/v10.0.13 |
| ONNX Runtime | 1.24.4, MIT | https://github.com/microsoft/onnxruntime/tree/v1.24.4 |
| Native ONNX WebGPU provider / Dawn | 0.4.0; bundled license and third-party notices | https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.EP.WebGPU/0.4.0 |
| DirectML | 1.15.4, Microsoft license in NuGet package | https://www.nuget.org/packages/Microsoft.AI.DirectML/1.15.4 |
| wgpu / naga | 30.0.1, MIT or Apache-2.0; locked transitive dependencies retain their terms | https://github.com/gfx-rs/wgpu |
| Burn / burn-onnx / CubeCL | Burn 0.22.0 with locked 0.22.1 components; MIT or Apache-2.0; Linux WSL inference and build-time ONNX import | https://github.com/tracel-ai/burn/tree/v0.22.0 |
| Visual C++ runtime | licensed Microsoft redistributable, not MIT | https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files |
| Rust / NuGet dependencies | locked versions; individual licenses | `engine/Cargo.lock`, project package references and restored package notices |
| Chart detector weights | ONNX metadata: Ultralytics 8.4.92 / AGPL-3.0 | [model notices](models/NOTICE.md) |
| Plane detector weights | ONNX metadata: Ultralytics 8.4.12 / AGPL-3.0 | [model notices](models/NOTICE.md) |
| Chart references | source/edition-specific reference data | [reference audit](docs/color-science-validation.md) |

This checkout processes still images. Current packages exclude FFmpeg/FFprobe;
older local rollback archives may still contain them and retain their original
notices. Runtime and model hashes are recorded in validation manifests.

Detector training provenance beyond supplied artifacts is not reconstructed or
invented. Known embedded metadata, maintainer authorization and remaining
limitations are recorded in [model provenance](docs/model-provenance.md).
The project's MIT license does not establish detector-weight or training-data
rights. Public packaging, signing and publication remain outside this local
implementation and require the user's confirmation.
