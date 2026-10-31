# Install on Apple-silicon macOS

Use **macOS 14 or later** on an Apple-silicon Mac. Download the `osx-arm64`
application; Intel Macs and Rosetta builds are not supported. The bundle contains
the native application, worker, models and private runtime libraries. You do not
need to install .NET, Rust or Python to use the desktop app.

## Download and open

1. Download `ColorCorrect-Studio-0.2.2-osx-arm64.zip` from the
   [0.2.2 release](https://github.com/collinswakholi/ColorCorrectionPackage_UI/releases/tag/v0.2.2).
   A `.tar.gz` alternative contains the same application bundle.
2. Compare the archive's SHA-256 with its entry in the release's
   `SHA256SUMS-osx-arm64.txt`. For example, in the download directory:

   ```sh
   shasum -a 256 ColorCorrect-Studio-0.2.2-osx-arm64.zip
   ```

3. Double-click the ZIP in Finder. Move **ColorCorrect Studio.app** to
   **Applications**, then open the app there. Keep the bundle intact; do not move
   individual executables or libraries out of it.

These downloads are **not Developer ID signed or notarized**. Their ad-hoc
signatures allow native code to run locally; they do not identify a publisher or
provide Apple's notarization approval.

If macOS blocks the first launch and you choose to trust this download, attempt
to open it once, then use **System Settings → Privacy & Security → Open Anyway**
and confirm the app. Follow [Apple's instructions for opening downloaded apps](https://support.apple.com/en-us/102445).
An organization-managed Mac may restrict this choice. Leave Gatekeeper and other
system security protections enabled.

## Processing choices

Leave detection on **Auto** to try WebGPU/Metal and recover to CPU inference when
needed. Advanced detection settings also offer explicit CPU inference. Image
correction has separate Auto/CPU/GPU choices and retains CPU recovery.

Graphics support comes with macOS; the app does not install a replacement system
driver. Use **Check setup** in device details to check the bundled engines.
Setup choices select optional acceleration components; **Install support** saves
those preferences locally, without installing system packages. Choose **Apply
engine change** afterward to restart the worker with the selected configuration.

Mac graphics and interactive desktop workflows remain experimental.
Native build and CPU checks do not establish every GPU, monitor or display-scale
configuration. See [compatibility](compatibility.md) for the current limits.

## Optional Python API

Install Python 3.10+ separately only if you want to use the SDK. Keep notebook
tools in their own environment. Install the wheel included with this app:

```sh
python3 -m venv "$HOME/.venvs/colorcorrect"
. "$HOME/.venvs/colorcorrect/bin/activate"
python -m pip install "/Applications/ColorCorrect Studio.app/Contents/MacOS/python/colorcorrect_studio-2.2.2-py3-none-any.whl"
```

Use the bundle's **Contents/MacOS** directory as the SDK root:

```python
from pathlib import Path
from colorcorrect import Client

app_root = Path("/Applications/ColorCorrect Studio.app/Contents/MacOS")
with Client(root=app_root) as client:
    calibration = client.calibrate(
        Path.home() / "Pictures/chart.jpg",
        output_dir=Path.home() / "Pictures/ColorCorrect/calibration",
    )
    result = calibration.apply(
        Path.home() / "Pictures/scene.jpg",
        output_dir=Path.home() / "Pictures/ColorCorrect/corrected",
    )
    print(result.outputs)
```

Use `Client(root=app_root, backend="cpu-inference")` for explicit CPU detection.
The wheel alone does not contain the worker or models. The [API guide](../api/README.md)
includes FFC, configuration, model reload and batch examples.

## Updates, troubleshooting and removal

Close the app before replacing its bundle with a newer version. Keep exported
models, sessions and corrected images outside the application bundle.

If a library or model is missing, extract the complete original archive again
instead of copying files from another release. For a wrong-architecture error,
check **About This Mac** and confirm that the downloaded filename ends in
`osx-arm64`. Report the application version and the error shown in device details
when requesting help. [Source build instructions](building.md#apple-silicon-macos-source-build)
remain available for local development.

To remove the desktop application, close it and move its app bundle to the Trash.
This does not remove your saved work or shared developer tools. To remove the
optional SDK, run `python -m pip uninstall colorcorrect-studio` in its virtual
environment; remove that environment separately when it is no longer needed.
