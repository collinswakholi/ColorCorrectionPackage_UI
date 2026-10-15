# Contributing

Start with [building.md](docs/building.md), the [migration boundaries](docs/migration.md)
and [color-science validation](docs/color-science-validation.md). Open a focused PR
with reproduction steps, tests and user-visible behavior. Keep mathematical,
UI and packaging changes separable. Preserve NDJSON and SDK 1.1 compatibility.

Do not commit private photos, sessions, machine paths, junctions, outputs or
executables. The five `test_images` files are the public sample corpus. Additional
coverage should use synthetic fixtures with documented encodings/orientations.
All detector weights are regular Git blobs; no individual file may reach 100 MiB.
Update their hash manifest and provenance deliberately, not automatically.

Review failed visual comparisons individually. Include before/after evidence;
never mass-refresh baselines merely to pass tests. SDK examples must run in fresh
notebook kernels against the packaged worker. Keep notebook source outputs empty.

Read-only PR checks cannot publish releases. The maintainer merges; the separate
manual workflow builds that exact merged commit and prepares an unsigned draft
prerelease only. Publication is a separate human review. See [releasing.md](docs/releasing.md).
