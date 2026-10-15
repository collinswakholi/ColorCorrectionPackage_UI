# Detector model notices

The project MIT license does not override model or upstream component terms.
The supplied Opt2 TorchScript exports explicitly identify **Ultralytics 8.4.92**
and **AGPL-3.0** in their preserved embedded export metadata. They also contain
generated Ultralytics module code. Retain [the AGPL license](LICENSE-AGPL-3.0.txt),
the original exporter notices and this notice when redistributing these files.
Upstream source/access: https://github.com/ultralytics/ultralytics/tree/v8.4.92
and https://github.com/ultralytics/ultralytics/archive/refs/tags/v8.4.92.zip.

On 14 October 2026, local export/training path prefixes were redacted from Opt2
description/config/debug metadata using `scripts/sanitize_model_metadata.py`.
All ONNX inference graphs/tensors and all TorchScript inference code, constants
and tensor storage entries were verified unchanged. This is not new training,
quantization, a different architecture, or a change of license. Original and
sanitized hashes are recorded in [model provenance](../docs/model-provenance.md).

The maintainer supplied and authorized distribution of these trained artifacts.
Dataset rights, base-weight ancestry and training-specific source/records were
not provided; they are not claimed verified. The exporter metadata identifies
the Opt2 framework and declared terms, not the provenance of every training input.
The plane detector's framework/terms are not inferred from its filename.

The LUT application graph is project-generated source under the project MIT
license. See [the manifest](manifest.json) for the exact shipped graph and weight
hashes, and the provenance document for generation and validation details.
