# Python tutorials

Use these notebooks to fit a chart photograph, adjust correction settings, and
apply a saved model. Install the ColorCorrect Studio SDK and keep the native
application package available. The notebooks can run from any directory.

- [Chart to calibrated images](01_headless_workflow.ipynb): editable FFC/GC/WB/CC
  options, detection, fitting, stage previews, batches, model reload, CSV
  references, and LUT export.
- [Compare fit methods](02_compare_fit_methods.ipynb): five CC families (six
  method choices), polynomial/PLS controls, neural topology and optimizers,
  training error versus CC-only held-out validation.

## Install the kernel environment

Use Python 3.10+ and the wheel included in the installed/extracted app's `python/`
folder. The SDK is independently versioned 2.2.1; use the wheel from your application package.

```sh
python -m venv .venv
# Activate this environment, then:
python -m pip install /path/to/ColorCorrectStudio/python/colorcorrect_studio-2.2.1-py3-none-any.whl
python -m pip install jupyterlab ipykernel Pillow
```

Jupyter and Pillow are used to run the notebooks and display images; the SDK
itself does not require them. Use the wheel from your application package
rather than an unrelated package named `colorcorrect`.

Create a durable output directory. Set these variables **before starting the
kernel** (use the equivalent native paths on your machine):

```sh
export COLORCORRECT_APP_ROOT=/opt/colorcorrect-studio
export COLORCORRECT_SAMPLES=/path/to/sample-photos
export COLORCORRECT_OUTPUT_DIR=/path/to/tutorial-results
export COLORCORRECT_REFERENCE_CSV=/path/to/classic-lab-d50.csv
python -m jupyterlab
```

In PowerShell:

```powershell
$env:COLORCORRECT_APP_ROOT = 'C:\path\to\ColorCorrectStudio'
$env:COLORCORRECT_SAMPLES = 'C:\path\to\test_images'
$env:COLORCORRECT_OUTPUT_DIR = 'C:\path\to\tutorial-results'
$env:COLORCORRECT_REFERENCE_CSV = 'C:\path\to\classic-lab-d50.csv'
python -m jupyterlab
```

Select the kernel belonging to this environment, then run cells from the top.
The setup cells show the SDK version, application location, and output directory.

Use the public repository's `test_images/` directory, containing
`ColorCard_Big.JPG`, `Blank.JPG`, `Sample_3572.JPG`, and `Sample_3573.JPG`.
`COLORCORRECT_REFERENCE_CSV` is optional for interactive use: without it, the
custom-reference exercise explicitly reports that it was not executed. It must
contain a complete 24-patch Classic/Passport Lab D50 profile; do not substitute a
photograph's measured values for independent reference measurements.

## Configuration and interpretation

Edit `Config` stage switches and `CalibrationOptions`/`FFCOptions` in notebook 1.
These frozen dataclasses are changed using `dataclasses.replace`. `Config`
overrides nested stage switches. GC/WB expose enable/disable controls and fit
their transforms automatically; there are no manual gamma or white-temperature
parameters. Notebook 2 shows polynomial degree, PLS components, and CC neural
settings. FFC neural settings are separate.

Chart and automatic white-plane detection need a supported GPU/driver.
`backend="cpu"` selects CPU image correction, not CPU chart inference. Standard
WSL packages do not supply the experimental Dozen add-on: follow the
[WSL installation notes](../installer/README.md#experimental-wsl-gpu-setup) and
identify any add-on variant when reporting results.

The SDK creates unique directories under your explicit output parent. Preserve
model JSON and the FFC white image for reuse; a cube LUT excludes spatial FFC.
Inspect warnings and actual backend diagnostics. CC-only cross-validation leaves
earlier stages fitted on the full chart; use independent captures to evaluate
full-pipeline accuracy. The timing cells are illustrative observations.

## Run the example as a script

Download [configured_pipeline.py](../api/examples/configured_pipeline.py) and run
it with your application, sample-photo, reference, and output paths:

```sh
python configured_pipeline.py --root /path/to/installed-app \
  --samples /path/to/sample-photos --output-dir /path/to/results \
  --reference-csv /path/to/classic-lab-d50.csv
```

The example includes custom references, partial-batch outcomes, saved-model FFC
reuse, an application-stage override, and LUT export. Edit its configuration
function to use different FFC or CC settings.

Contributor instructions for automated notebook execution and the versioned
execution record are in [developer validation](../docs/api/VERIFICATION.md).

[API guide](../api/README.md) · [Configuration reference](../docs/api/README.md#configuration-reference)
· [Original study](https://doi.org/10.1002/ppj2.70067)
· [ColorCorrectionPipeline](https://pypi.org/project/ColorCorrectionPipeline/)
