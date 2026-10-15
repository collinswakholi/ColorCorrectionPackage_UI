# Migrating from the web application

Repository history, older releases, the repository URL, and MIT attribution are
retained. The final web commit is `c790f8af459ca522090bfc92519fdd4398ec7545`,
preserved on `legacy-web` and the annotated `legacy-web-final` tag. Do not move
that tag. The reviewed migration is recorded in
[PR #1](https://github.com/collinswakholi/ColorCorrectionPackage_UI/pull/1).
It imports the native code at the repository root (`app/`, `engine/`, `api/`,
`models/`, `refs/`, `notebooks/`, and `test_images/`), not inside a nested
`CCP_New_UI` directory. The legacy implementation remains available from the
archive branch and tag rather than as the active native app tree.

The native Windows installer is the current
[v0.1.0 main release](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.1.0),
also selected by `/releases/latest`. On 15 October 2026 the maintainer promoted
the tested public build from prerelease to Latest. Its installer, source tag,
checksums, and original build metadata are unchanged. Match `build-info.json`
to the `v0.1.0` tag, even as later documentation commits advance `main`.

| Legacy workflow | Native workflow |
| --- | --- |
| Browser + Flask HTTP server | Avalonia desktop; no HTTP listener |
| REST processing calls | Sequential NDJSON worker / Python SDK 1.1 |
| `.pkl` serialized Python estimators | Refit chart calibration and save native model JSON |
| Node/Python development environment | Pinned .NET/Rust; Python only for SDK/tutorials |
| Docker and older OS installers | Retired legacy artifacts; native v0.1.0 binary is Windows 11 x64 only |

Do not load untrusted pickle files. They can execute Python code, and estimator
versions differ. There is deliberately no converter. Keep the old app available
to inspect its settings, then refit original chart/blank images in the native app.
Inspect chart orientation, type/edition and held-out error before reusing the fit.
Native JSON is not a renamed pickle and legacy sessions are not interchangeable.

Install ColorCorrect Studio alongside ColorCorrector. The installer IDs and app
folders differ; native uses internal `CCP-New` storage while the legacy Python
app uses `.colorcorrector`. Previous GitHub releases carry retirement notices
and a link to v0.1.0; their original downloads and tags remain available. Existing
Docker Hub images are historical web builds.
Backup your originals and calibration files before migrating a workflow.

Still-image SDK operations/options and the native NDJSON transport are preserved.
Video capabilities are removed from this release, including media tools and UI
controls. Former `video`/`video_grade` names return an explicit unsupported error
to older clients; installing FFmpeg does not restore them. Global `.cube` model
export remains available and does not include spatial FFC.
See [API examples](api/README.md). Bundle paths are relative to the installed app,
but caller-supplied relative input/output paths are relative to the caller's cwd.
Copy notebooks to a writable working directory; set `COLORCORRECT_ROOT` to the
installation folder when it is not an ancestor of the notebook working directory.
