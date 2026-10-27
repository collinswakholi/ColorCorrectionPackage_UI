# ColorCorrect Studio

Start with [installation and prerequisites](INSTALL.txt), then follow the
[illustrated user guide](docs/user-guide.md). Keep this complete folder together;
the desktop application needs no Python, Rust or separate .NET installation.

## Graphics and platforms

Chart and plane detection need a compatible GPU and driver. CPU correction can
apply a saved model when GPU inference is unavailable. No installer replaces
system graphics drivers.

This is an unsigned preview build. ARM64 graphics and desktop workflows remain
experimental. WSL GPU use needs the optional, separately prepared x64 add-on,
which is disabled until explicitly enabled. See INSTALL.txt for setup controls.
The application processes still images. Read [compatibility](docs/compatibility.md)
and [processing options](docs/compute/README.md) for mode and platform details.

## Optional Python API

The wheel is in `python/`. Install it into your Python environment with
`python -m pip install --no-deps path/to/the-wheel.whl`, then use
`Client(root="/path/to/this/folder")`. Runnable examples are in `python/examples/`.
The [quickstart](api/README.md) and [Python guide](docs/api/README.md) explain
configuration, model reuse and batch application. Source-build commands in those
guides apply to a separate repository checkout.

## Files and licenses

`package-manifest.json` lists the application files and their checksums.
`runtime-support.json` contains requirements and platform notes for this build;
`dependency-report.json` provides library details for troubleshooting.
License and notice texts are under `licenses/` and beside the models/runtime.
[Model provenance](docs/model-provenance.md) describes the model sources and
remaining training-data uncertainties. Preserve these notices with the application.
