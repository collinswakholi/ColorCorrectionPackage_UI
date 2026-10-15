# Windows release procedures and original validation gates

The maintainer promoted **v0.1.0 to the main (Latest) release on 15 October 2026**,
after its public preview and completed release validation. The GitHub prerelease
flag is now false. Its installer, tag, checksums, and build provenance are
unchanged; `preview: true` in the original build record describes how that binary
was first built, not the current GitHub designation. Check
[PR #1](https://github.com/collinswakholi/ColorCorrectionPackage_UI/pull/1) and the
[v0.1.0 release page](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.1.0)
for the actual migration/publication state. No main push, tag trigger or scheduled
workflow publishes binaries or Docker images. Older GitHub releases are labeled
retired and link to v0.1.0; their assets and tags remain available. Existing Docker
images remain historical web builds.

Only `windows-preview.yml` uses release-write permission, in its final draft job.
Invoke it from main with the exact 40-character merged main SHA. It rejects an
unmerged/different/dirty source state and refuses to overwrite an existing v0.1.0.
The original v0.1.0 workflow creates a **draft + prerelease** for review. That
one-time workflow refuses to recreate the existing release; promotion was an
explicit maintainer action after publication. For future versions, review the
source identity, installer, assets, and checksums before publishing, then set
`prerelease: false` and `make_latest: "true"` only for an approved main release.
Publication and promotion remain separate from automated builds.
Configure the `native-preview` GitHub environment with required reviewers if your
repository settings support them. No automatic merge is enabled.

The original `APPROVED` validation gates are retained below. Their completed,
scoped evidence is linked from the release and migration PR:

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

1. Complete and review all gates above, including the live modal
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
5. Publish the reviewed draft with the maintainer's approved release designation.
   Recheck that the explicit release page and download links resolve publicly,
   that the downloaded installer hash matches, and that `main` contains the
   native source. Retire superseded releases with a visible migration notice;
   preserve historical assets and tags. Retiring a release does not validate
   another platform or change the binary's signature status.

## Platform status

| Target | Source/scripts | Validation / distribution |
| --- | --- | --- |
| Windows 11 x64 | Pinned .NET 10 / Rust; self-contained Setup | v0.1.0 published as Latest; scoped live/clean-Win11 evidence on migration PR |
| Linux x64 | Native-host tarball script retained; CPU SDK isolated | Needs native-host build/install/workflow validation; no v0.1.0 binary |
| macOS arm64 | Native-host .app script retained; ad-hoc signature | Needs Apple Silicon build/install validation; not notarized; no v0.1.0 binary |

Never represent script syntax checks or retained code as platform certification.
