# Building and publishing a release

The native workflow builds executable Windows/Linux applications automatically.
A version tag publishes the same revision only after all native checks pass.
Apple-silicon macOS remains source-only; no Mac app, runtime, installer, wheel,
or application archive is uploaded or attached to a release.

## Automatic builds

Pull requests and updates to `main` run four native package jobs: Windows 11 x64,
Windows 11 ARM64, Ubuntu 24.04 x64, and Ubuntu 24.04 ARM64. A fifth job builds and
tests the source on Apple-silicon macOS. The Windows jobs produce Setup installers
and portable ZIPs. Linux jobs produce Debian packages and portable tar archives.

Each package job checks native architecture, required notices, runtime/model
hashes, portable payload hashes, actual installation, component suites, an
isolated installed Python wheel/worker smoke test, and application launch.
Artifacts and evidence are available from the Actions run. Main/PR builds have
read-only repository permissions and do not publish releases.

## Publish a preview version

1. Update the native version coherently in the application, shared .NET properties,
   internal Rust packages, and Cargo lockfile. The SDK has its own version and
   does not need a version bump unless it changes. Update the changelog and README
   download links. Source/version tests check consistency.
2. Merge the reviewed revision into `main`. Keep essential regressions, notices,
   public fixtures and source provenance; omit private sessions and build caches.
3. Tag that exact main revision. For example, after fetching and checking `main`:

   ```sh
   git switch main
   git pull --ff-only
   git tag -a v0.2.0 -m "ColorCorrect Studio 0.2.0 preview"
   git push origin v0.2.0
   ```

4. The tag push runs the five native jobs again. Only after every job succeeds
   does the publication job receive `contents: write`. It verifies that the live
   tag still matches this run, the tag equals the native application version,
   and its commit belongs to `main`.
5. The publisher downloads exactly four candidate artifacts from that run,
   verifies their GitHub archive digests, source/version/architecture manifests,
   and distribution checksums, then prepares the release assets. It creates a
   draft, verifies every uploaded asset, and publishes it as a **prerelease**.
   It does not replace the previous stable Latest release.

The eight application downloads retain their versioned filenames. Each target
also has `SHA256SUMS-<rid>.txt` and `package-manifest-<rid>.json`. A combined
`SHA256SUMS` covers the other sixteen assets. Python wheels are included inside
native application packages, not published separately to PyPI.

The publisher does not move tags, delete assets, or overwrite an existing
published release. A failed upload leaves a draft; rerunning the failed publish
job resumes only if that draft and its existing assets match the same source
and bytes. A mismatch requires investigation, not a forced overwrite. Published
releases are verified and left unchanged on a successful retry.

## Validation and release status

A preview does not imply native GPU, driver, interactive UI, high-DPI, clipboard,
or full workflow certification on every target. Hosted runners test installation
and CPU application without claiming GPU inference. Preserve explicit skips,
failures and hardware gaps in the evidence. Windows Sandbox and WSL GPU/UI checks
retain their own package hashes; they do not certify ARM hardware or macOS GPUs.

Stable promotion, signing, or a new package-feed publication is a separate
maintainer decision. Complete the corresponding native acceptance and review
runtime/model redistribution notices and disclosed provenance limitations first.
See [native acceptance](native-validation.md), [model provenance](model-provenance.md),
and [installation details](../installer/README.md).

Do not remove original user files, private research or raw diagnostics during
cleanup. Use the manifest-based source exporter and the explicit cache inventory
in `scripts/cleanup_local.py`; keep essential regression tests in the repository.
