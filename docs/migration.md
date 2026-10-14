# Migrating from the web application

Repository history, older releases, the repository URL, and MIT attribution are
retained. The final web commit is `c790f8af459ca522090bfc92519fdd4398ec7545`,
preserved on `legacy-web` and the annotated `legacy-web-final` tag. Do not move
that tag. The migration is reviewed on `codex/native-overhaul`; main is not changed
until its PR is approved and merged by the maintainer.

| Legacy workflow | Native workflow |
| --- | --- |
| Browser + Flask HTTP server | Avalonia desktop; no HTTP listener |
| REST processing calls | Sequential NDJSON worker / Python SDK 1.1 |
| `.pkl` serialized Python estimators | Refit chart calibration and save native model JSON |
| Node/Python development environment | Pinned .NET/Rust; Python only for SDK/tutorials |
| Docker and older OS installers | Existing artifacts remain legacy; first native preview is Windows 11 x64 |

Do not load untrusted pickle files. They can execute Python code, and estimator
versions differ. There is deliberately no converter. Keep the old app available
to inspect its settings, then refit original chart/blank images in the native app.
Inspect chart orientation, type/edition and held-out error before reusing the fit.
Native JSON is not a renamed pickle and legacy sessions are not interchangeable.

Install ColorCorrect Studio alongside ColorCorrector. The installer IDs and app
folders differ; native uses internal `CCP-New` storage while the legacy Python
app uses `.colorcorrector`. Neither old releases nor Docker Hub tags are altered.
Backup your originals and calibration files before migrating a workflow.

SDK operations/options and the existing native NDJSON protocol are preserved.
See [API examples](api/README.md). Bundle paths are relative to the installed app,
but caller-supplied relative input/output paths are relative to the caller's cwd.
Copy notebooks to a writable working directory; set `COLORCORRECT_ROOT` to the
installation folder when it is not an ancestor of the notebook working directory.
