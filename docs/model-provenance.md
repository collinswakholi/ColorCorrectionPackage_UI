# Model and sample provenance

The maintained detector computation comes from the supplied native application. The pinned upstream revision is `58eedc98a8a333c62658b69f2da4faff5e764d40`; its provenance records prior maintainer authorization to distribute the supplied models and five public sample photographs. This does not independently establish training-data rights.

[The model manifest](../models/manifest.json) pins the exact three shipped ONNX files by size and SHA-256. All are below 100 MiB. No detector training, quantization, operator changes or learned-weight replacement was performed.

## Declared upstream terms

The ONNX metadata itself identifies:

| Detector | Framework/export metadata | Declared license |
| --- | --- | --- |
| Chart 1280 and 1536 | Ultralytics 8.4.92, YOLO11s OBB | AGPL-3.0 |
| Plane 512 | Ultralytics 8.4.12, YOLOv8s | AGPL-3.0 |

The plane attribution is based on inspected metadata, not its filename. Preserve [model notices](../models/NOTICE.md) and [the AGPL text](../models/LICENSE-AGPL-3.0.txt). The project MIT license does not override model terms. Training data permissions, base-weight ancestry and training-specific source records remain unverified; no comprehensive redistribution certification is claimed.

## Metadata-only sanitation

On 20 October 2026, private user-directory prefixes were removed from chart model metadata. Original bytes and the detailed audit remain in the ignored local evidence directory. Both portable hashes match the already-sanitized pinned upstream artifacts.

| Model | Original SHA-256 | Portable SHA-256 |
| --- | --- | --- |
| Chart 1280 | `2c36420bc09c263bfdee155a737b452ed4c6395fd5449e7b5bc1e713719f7473` | `1d2e9cfca6c13922ad16abeb196b0ad3071778c0067bc9ea95c83b0e76ef2240` |
| Chart 1536 | `10f06c8d1f145c22a7d91c7275505cca322d83c4736e49cdf7b7e9ab03eac1e8` | `b1dc24fd86659e1e8927e0f8525290c806e2005eee0b333f52ef4a47604227ae` |
| Plane 512 | `51aacaf5424c85a8351e9e89d9e6e41892b0c56fbe3a61472ba7e4340b1837aa` | unchanged |

The maintenance script compares the complete serialized ONNX model after clearing only top-level metadata properties. Graphs, subgraphs, functions, operators/opsets, IR version, input/output signatures, initializer types/dimensions/tensor bytes and external-data declarations must match exactly. No external tensor companion is used. Author, license and class metadata remain intact. Embedded Rust inference must be rebuilt after a file-hash change, even when computation is identical.

## Samples and references

Public photographs are demonstration and regression inputs, not independent physical color truth. A blank-field photo is an FFC asset and a no-chart negative, not a color reference. Private photos, sessions, downloaded research images and derived personal reports remain ignored locally.

Canonical chart values retain their original edition, cell ordering and colorimetry.
[Reference sources and colorimetry](color-science-validation.md) records their
origins and limits. Production detector files are listed in the model manifest;
archived exports and research runtimes are not application dependencies.
