# Building and publishing a release

The native workflow builds Windows/Linux x64 and ARM64 packages and an
Apple-silicon macOS application. Main-branch and pull-request runs produce
development candidates. A version tag can publish a full release only after the
native validation jobs pass.

## Automatic builds

Five native jobs build the release targets:

| Target | Runner | Outputs |
| --- | --- | --- |
| Windows 11 x64 | `windows-2025` | Setup installer, portable ZIP |
| Windows 11 ARM64 | `windows-11-arm` | Setup installer, portable ZIP |
| Linux x64 | `ubuntu-22.04` | `.deb`, portable `.tar.gz` |
| Linux ARM64 | `ubuntu-22.04-arm` | `.deb`, portable `.tar.gz` |
| macOS 14+ (Apple silicon) | `macos-15` | ZIP and `.tar.gz` of **ColorCorrect Studio.app** |

Linux builds run on Ubuntu 22.04 so the binaries need only glibc 2.35. Each Linux
package is then installed on bare Ubuntu 24.04 and 22.04 and Debian 13 and 12
images: `check-system` must first fail, `install-dependencies` must fix it, and
the app must reach "Engine ready" without loading a library outside its
dependency list. There is no Intel Mac target.

Each job verifies architecture, private runtimes, notices, model/resource hashes,
package contents and version metadata. Installed Python workflows use the wheel
and worker belonging to that package. Application launch and CPU checks are
separate from hardware GPU and interactive desktop acceptance.

Main and pull-request jobs use read-only repository permissions and do not
publish releases; their artifacts and evidence are on the Actions run. A change
that touches only the root README or demo media runs the documentation checks
alone. Version tags and manual runs always run the complete native workflow.

## Publish a version

Publishing requires maintainer authorization. Application 0.5.0 includes SDK
**3.0.0**, unchanged from 0.4.0. Each desktop package carries the SDK wheel for
use with its own native worker.

1. Update native versions together: shared .NET properties, application metadata,
   the four internal Rust packages and their Cargo lock entries. The SDK has its
   own version in `api/pyproject.toml` and `api/colorcorrect.py`. Give changed SDK
   bytes a new version; do not distribute them under an older wheel filename.
   Update the citation, date the changelog heading (it reads "unreleased" until
   then) and update the download links in `README.md`, `installer/README.md` and
   `docs/macos-install.md`.
2. Review and merge the intended revision into `main`. Preserve essential
   regressions, public fixtures, notices and source provenance. Exclude private
   sessions and reproducible build caches.
3. Tag that exact main revision after checking it locally. For this release:

   ```sh
   git switch main
   git pull --ff-only
   git tag -a v0.5.0 -m "ColorCorrect Studio 0.5.0"
   git push origin v0.5.0
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

No publisher signing credentials have been supplied for 0.5.0. Windows/Linux
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
