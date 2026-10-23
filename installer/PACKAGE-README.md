# ColorCorrect Studio

Start with [installation and prerequisites](INSTALL.txt), then follow the
[illustrated user guide](docs/user-guide.md). Keep this complete folder together;
the desktop application needs no Python, Rust, or separate .NET installation.

The optional Python wheel is in `python/`. Install that wheel with
`python -m pip install --no-deps path/to/the-wheel.whl`; use
`Client(root="/path/to/this/folder")` from another working directory. The
[Python guide](docs/api/README.md) and [quickstart](api/README.md) include source
checkout build instructions as well; those source commands apply to a separate
repository checkout. Runnable examples are in `python/examples/`.

Processing supports still images. Detection needs a compatible hardware GPU;
saved-model application retains CPU fallback. The optional WSL x64 add-on is
experimental and disabled until explicitly enabled. See INSTALL.txt for controls.

`package-manifest.json` records payload hashes. `dependency-report.json` records
native library inspection. `licenses/dependencies/manifest.json` maps each active
Rust or published NuGet component to its exact collected license/notice texts;
other runtime, model and add-on notices are adjacent to their components.
[Model provenance](docs/model-provenance.md) discloses remaining source-data gaps.
Unsigned local candidates remain subject to the native acceptance and
redistribution gates in `runtime-support.json`; package creation is not platform
certification. No installer replaces system GPU drivers.
