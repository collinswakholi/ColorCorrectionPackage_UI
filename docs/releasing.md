# Windows preview release gates

The maintainer has authorized a **public Windows v0.1.0 prerelease**, after the
native migration and the release gates below are complete. Authorization does
not waive validation or permit substituting an earlier QA installer. Check
[PR #1](https://github.com/collinswakholi/ColorCorrectionPackage_UI/pull/1) and the
[v0.1.0 release page](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.1.0)
for the actual migration/publication state. No main push, tag trigger or scheduled
workflow publishes binaries or Docker images. Old releases/images are preserved.

Only `windows-preview.yml` uses release-write permission, in its final draft job.
Invoke it from main with the exact 40-character merged main SHA. It rejects an
unmerged/different/dirty source state and refuses to overwrite an existing v0.1.0.
It creates a **draft + prerelease**. Review its final source identity, installer,
assets, and checksums before explicitly publishing the authorized preview.
Keep the release marked **prerelease**, and do not change it to stable/latest or
overwrite existing releases. Publication is a separate, authorized action; the
workflow never publishes automatically.
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
  loaded-model application, exports, help and themes. Include interactive NN
  editing (single/array layers, optimizer, batch normalization, dropout),
  persistence of accepted settings, and session save/reload through the actual
  file pickers. Record outcomes and any cancellation/error behavior; headless
  coverage alone does not close these live UI gates. Former video commands must
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

## Source-matched publication checklist

1. Complete and review all gates above, including the outstanding live modal
   checks. Attach sanitized evidence to the migration PR; do not publish private
   paths, user sessions, attachments, or unrelated desktop captures.
2. Merge the reviewed native migration into `main` without rewriting legacy
   history. Record the exact full merged commit SHA, not merely a branch name.
3. Run the manual workflow with that current `main` SHA and `APPROVED` only when
   the recorded evidence justifies every gate. Its clean-checkout rebuild, not
   a previously built local executable, supplies the release assets.
4. Verify that `build-info.json` records the requested SHA and release eligibility,
   that the tag resolves to that SHA, and that the uploaded `.exe` SHA-256 agrees
   with its uploaded `.sha256`. Inspect the final installer and all required
   asset names before public publication.
5. Publish the reviewed draft as the authorized public **v0.1.0 prerelease**.
   Recheck that the explicit release page and download links resolve publicly,
   that the downloaded installer hash matches, and that `main` contains the
   native source. Leave old releases and Docker images unchanged.

## Platform status

| Target | Source/scripts | Validation / distribution |
| --- | --- | --- |
| Windows 11 x64 | Pinned .NET 10 / Rust; self-contained Setup | Migration evidence records local QA; clean Win11 gate still explicit |
| Linux x64 | Native-host tarball script retained; CPU SDK isolated | Needs native-host build/install/workflow validation; no v0.1.0 binary |
| macOS arm64 | Native-host .app script retained; ad-hoc signature | Needs Apple Silicon build/install validation; not notarized; no v0.1.0 binary |

Never represent script syntax checks or retained code as platform certification.
