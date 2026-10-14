# Third-party software and data

The repository's MIT license covers this project's source, not every bundled
component. Runtime archives and SDK dependencies retain their upstream terms.
Installed `licenses/` contains copied notices and generated dependency inventories.
The following are principal components, not a substitute for those full notices.

| Component | Version / terms | Source/access |
| --- | --- | --- |
| Avalonia | 12.0.2, MIT | https://github.com/AvaloniaUI/Avalonia |
| .NET | SDK 10.0.402; runtime 10.0.13, MIT and third-party notices | https://github.com/dotnet/runtime/tree/v10.0.13 |
| LibTorch / PyTorch | 2.9.1, BSD-style + bundled third parties | https://github.com/pytorch/pytorch/tree/v2.9.1 |
| ONNX Runtime | Windows DirectML 1.22.1, MIT | https://github.com/microsoft/onnxruntime/tree/v1.22.1 |
| DirectML | 1.15.4, Microsoft license in NuGet package | https://www.nuget.org/packages/Microsoft.AI.DirectML/1.15.4 |
| Visual C++ runtime | licensed Microsoft redistributable, not MIT | https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files |
| FFmpeg full Windows build | Gyan 9.0, GPL v3; separate process | https://github.com/GyanD/codexffmpeg/releases/tag/9.0 |
| Rust / NuGet dependencies | locked versions; individual licenses | generated inventory in `licenses/` |
| Detector weights / chart references | see provenance and reference notices | [models](docs/model-provenance.md), [reference audit](docs/color-science-validation.md) |

FFmpeg's static full build includes numerous externally licensed libraries. Its
upstream LICENSE and README preserve its complete configuration/version inventory.
The FFmpeg source commit is `d32b387f2b`; source access is
https://github.com/FFmpeg/FFmpeg/tree/d32b387f2b. **This alone is not a complete
corresponding-source bundle for all statically linked components.** Before public
release, supply and review corresponding source/build material for that exact
binary and its applicable components, retaining their licenses. The draft release
workflow fails closed until a checksum-pinned corresponding-source archive is
configured and the release checklist is attested by the maintainer. A URL to
FFmpeg alone is not treated as legal clearance.

Detector training provenance beyond supplied artifacts is not reconstructed or
invented. Maintainer redistribution authorization and known limitations are
recorded in [model provenance](docs/model-provenance.md). Review weights and any
training-framework-derived obligations before public publication.
