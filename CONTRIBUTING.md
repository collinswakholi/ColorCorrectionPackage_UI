# Contributing

Start with [building](docs/building.md), [compatibility](docs/migration.md) and [color-science validation](docs/color-science-validation.md). Keep mathematical, UI and packaging changes reviewable separately. Preserve existing Engine imports, the NDJSON contract and saved-model/session behavior.

Run `python scripts/export_source.py --check` before preparing source. It inspects current files, including unstaged and untracked additions; it does not export an old Git HEAD. Keep notebooks unexecuted, model hashes pinned and visual baselines individually reviewed. Do not add private photos, machine paths, credentials, runtime binaries, junctions or generated outputs.

Keep regression tests for color math, reference identities, FFC, batch recovery, worker lifecycle and package integrity. Research programs and raw evidence belong in ignored local folders. `python scripts/cleanup_local.py` reports reproducible caches without deleting anything; perform cleanup only after final validation and review.

The build/workflow contract tests use Python 3.12.10 and the hashed build-only
requirements in `scripts/requirements-ci.txt`; see the component commands in
the build guide. Native product and SDK versions are checked as separate groups.

Model license notices apply independently of the project's MIT source license. Follow [model provenance](docs/model-provenance.md) when sanitizing metadata; never replace learned weights implicitly.

CI builds and uploads validation artifacts only. Publishing, signing, commits and pushes require explicit maintainer action. See [release preparation](docs/releasing.md).
