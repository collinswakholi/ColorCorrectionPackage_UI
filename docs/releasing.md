# Windows preview release gates

Current status: **v0.1.0 native preview not published**. Migration PR must be
reviewed and merged by the maintainer. No main push, tag trigger or scheduled
workflow publishes binaries or Docker images. Old releases/images are preserved.

Only `windows-preview.yml` uses release-write permission, in its final draft job.
Invoke it from main with the exact 40-character merged main SHA. It rejects an
unmerged/different/dirty source state and refuses to overwrite an existing v0.1.0.
It creates a **draft + prerelease**; public publication is a human review step.
Configure the `native-preview` GitHub environment with required reviewers if your
repository settings support them. No automatic merge is enabled.

Before entering `APPROVED`, record evidence for every gate:

- [ ] Fresh checkout; pinned .NET/Rust; no private paths, junctions, cached model
  pointers or installed developer runtime dependence. Five public sample hashes unchanged.
- [ ] Rust/.NET/Python, reference CIEDE2000 vectors, 64 conversion cases, ICC/EXIF,
  NN options/roundtrips and missing-FFC rejection pass. Visual changes reviewed individually.
- [ ] All six methods, stage previews, saved-model application and exports pass.
  Both clean notebook sources execute against the actual staged/installed worker.
- [ ] Live UI walkthrough: catalog/session, arrows, overlays, settings, fitting,
  loaded-model application, exports, help and themes. Former video commands must
  return an explicit unsupported error, and video controls must not be present.
- [ ] CPU-only clean Windows 11 x64 install, offline samples, legacy side-by-side,
  reinstall and uninstall without user-data deletion. Hosted `windows-2022`
  runners are Windows Server, **not proof of a clean Windows 11 installation**.
- [ ] Available CUDA/DirectML checks report actual backend; unavailable checks
  clearly marked. Do not claim GPU NN training or universal acceleration.
- [ ] All installer files/checksums/provenance/third-party notices reviewed.
- [ ] No FFmpeg/ffprobe or video dependency is included in the installer or
  release assets. Detector notices, license/source access and redistribution
  obligations reviewed; the project's MIT license does not cover all weights.

The native preview is still-image-only. Its workflow has no FFmpeg download or
corresponding-source archive variables because that component is not shipped.
This removes the former FFmpeg publication gate; it does not waive obligations
for the detector weights or any remaining bundled dependency. Review the model
disclosures and complete dependency inventory before entering `APPROVED`.

Assets: `ColorCorrect-Studio-win-x64.exe`, its `.sha256`, `build-info.json`,
`dependency-inventory.json`, `THIRD_PARTY_NOTICES.md`, `model-provenance.md`,
`NOTICE.md` (model notices), and `LICENSE-AGPL-3.0.txt` (model upstream license).
No portable ZIP, Linux/macOS binaries or Docker pushes in this first release.
Local dirty-worktree installers are QA artifacts and are never eligible for release.

## Platform status

| Target | Source/scripts | Validation / distribution |
| --- | --- | --- |
| Windows 11 x64 | Pinned .NET 10 / Rust; self-contained Setup | Migration evidence records local QA; clean Win11 gate still explicit |
| Linux x64 | Native-host tarball script retained; CPU SDK isolated | Needs native-host build/install/workflow validation; no v0.1.0 binary |
| macOS arm64 | Native-host .app script retained; ad-hoc signature | Needs Apple Silicon build/install validation; not notarized; no v0.1.0 binary |

Never represent script syntax checks or retained code as platform certification.
