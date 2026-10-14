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
| Rust / NuGet dependencies | locked versions; individual licenses | generated inventory in `licenses/` |
| Opt2 detector weights | embedded metadata records Ultralytics 8.4.92 / AGPL-3.0 | [model notices](models/NOTICE.md), [provenance/source access](docs/model-provenance.md) |
| Plane detector / chart references | see provenance and reference notices | [models](docs/model-provenance.md), [reference audit](docs/color-science-validation.md) |

The native preview processes still images only. FFmpeg/ffprobe and video
capabilities have been removed; those executables are not downloaded or bundled
by the packaging scripts. This does not alter older releases or their notices.

Detector training provenance beyond supplied artifacts is not reconstructed or
invented. Known embedded metadata, maintainer authorization and remaining
limitations are recorded in [model provenance](docs/model-provenance.md).
The installer preserves model notices and the AGPL-3.0 license text; this does
not turn the weights into MIT-licensed assets or establish undocumented dataset
rights. Review the disclosed obligations before public publication.
