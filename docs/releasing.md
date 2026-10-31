# Building and publishing a release

The native workflow builds Windows/Linux x64 and ARM64 packages and an
Apple-silicon macOS application. Main-branch and pull-request runs produce
development candidates. A version tag can publish a full release only after the
native validation jobs pass.

## Automatic builds

Five native jobs cover Windows 11 x64, Windows 11 ARM64, Ubuntu 24.04 x64,
Ubuntu 24.04 ARM64 and Apple-silicon macOS 14+. Windows produces Setup installers
and portable ZIPs. Linux produces Debian packages and portable tar archives.
macOS produces ZIP and tar archives containing **ColorCorrect Studio.app**;
there is no Intel Mac target.

Each job verifies architecture, private runtimes, notices, model/resource hashes,
package contents and version metadata. Installed Python workflows use the wheel
and worker belonging to that package. Application launch and CPU checks are
separate from hardware GPU and interactive desktop acceptance.

Main and pull-request jobs use read-only repository permissions and do not
publish releases. Artifacts and their evidence are available from the Actions
run. Root README/demo-only edits retain lightweight documentation checks;
version tags and manual runs always use the complete native workflow.

## Publish a version

Publishing requires maintainer authorization. **0.2.2** is the next application
release and includes SDK **2.2.2**. Existing 0.2.1 and earlier assets remain
unchanged.

1. Update native versions together: shared .NET properties, application metadata,
   the four internal Rust packages and their Cargo lock entries. The SDK has its
   own version in `api/pyproject.toml` and `api/colorcorrect.py`. Give changed SDK
   bytes a new version; do not distribute them under an older wheel filename.
   Update the citation, changelog and download instructions.
2. Review and merge the intended revision into `main`. Preserve essential
   regressions, public fixtures, notices and source provenance. Exclude private
   sessions and reproducible build caches.
3. Tag that exact main revision after checking it locally. For this release:

   ```sh
   git switch main
   git pull --ff-only
   git tag -a v0.2.2 -m "ColorCorrect Studio 0.2.2"
   git push origin v0.2.2
   ```

4. The tag run repeats all five native jobs. The publishing job receives
   `contents: write` only after they succeed. It checks that the live tag still
   matches the run, the tag equals the native version, and the revision belongs
   to `main`.
5. The publisher uses the candidate artifacts from that same run. It verifies
   their GitHub archive digests, source/version/architecture manifests and
   distribution checksums. It uploads to a draft, verifies the uploaded bytes,
   then publishes the authorized full release and marks it Latest.

Application filenames include their version and target architecture. Each target
also provides `SHA256SUMS-<rid>.txt` and `package-manifest-<rid>.json`; a combined
`SHA256SUMS` covers the release assets. SDK wheels are included inside native
application packages, not published separately to PyPI.

The publisher must not move tags, delete assets or overwrite a published release.
A failed upload leaves a draft. A retry may resume only when the draft and its
existing assets match the same source and bytes. A mismatch requires
investigation. A successfully verified existing release is left unchanged.

## Signing and platform status

No publisher signing credentials have been supplied for 0.2.2. Windows/Linux
packages are unsigned. Mac bundles use ad-hoc signatures for native execution;
they are **not Developer ID signed or notarized**. Follow the
[Mac installation guide](macos-install.md) for the security prompt. Do not disable
Gatekeeper or system driver security to make a package launch.

Later signing or notarization requires a separately versioned release and its own
verification. Do not silently replace an existing asset with signed bytes under
the same filename. Package-feed publication is also a separate maintainer action.

A full-release designation does not certify every GPU, driver, interactive UI,
clipboard or high-DPI configuration. Hosted checks establish their recorded
build/install/CPU scope. Keep unavailable hardware and unexecuted cases explicit;
Windows Sandbox and WSL evidence does not certify physical ARM devices or Mac
GPUs. See [native acceptance](native-validation.md),
[model provenance](model-provenance.md) and [installation details](../installer/README.md).

Use the manifest-based source exporter and explicit cache inventory in
`scripts/cleanup_local.py` when preparing the checkout. Preserve original user
files, private research, raw diagnostics and essential regression tests.
