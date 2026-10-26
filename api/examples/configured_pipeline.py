"""Installed-SDK example: editable stages, custom references, reload, and batches.

Copy this file to a neutral directory and run it with the installed wheel.
No checkout import, Python ML library, GUI, or server is required.
"""
from __future__ import annotations

import argparse
from dataclasses import replace
import json
from pathlib import Path
import sys
import tempfile

import colorcorrect
from colorcorrect import (
    ApplyOptions, CalibrationOptions, ColorCorrection, Config, FFCOptions,
    NeuralNetworkOptions, ReferenceProfile,
)


def tutorial_config(output_dir: Path) -> Config:
    """Change stage switches here; change method details in CalibrationOptions."""
    return Config(
        save_path=output_dir, do_ffc=True, do_gc=True, do_wb=True, do_cc=True,
        options=CalibrationOptions(
            model="rpcc2", cross_validate=True,
            ffc=FFCOptions(method="pls", bins=32, smooth=5, degree=3),
            crop_rect=None,  # Or x1,y1,x2,y2 in the matching white-image pixels.
        ),
    )


def alternative_configs(config: Config) -> dict[str, Config]:
    """Five CC families, six choices; building options does not launch a worker."""
    return {
        "ccm": replace(config, options=replace(config.options, model="ccm")),
        "linear": replace(config, options=replace(config.options, model="linear", degree=2)),
        "rpcc2": config,
        "rpcc3": replace(config, options=replace(config.options, model="rpcc3")),
        "pls": replace(config, options=replace(config.options, model="pls", pls_components=3)),
        "nn": replace(config, options=replace(config.options, model="nn",
            nn=NeuralNetworkOptions(hidden_layers=(64, 32), epochs=200, patience=50))),
    }


def run(root: Path, samples: Path, output_dir: Path, reference_csv: Path) -> dict:
    output_dir.mkdir(parents=True, exist_ok=True)
    out = Path(tempfile.mkdtemp(prefix="configured-pipeline-", dir=output_dir.resolve()))
    config = tutorial_config(out)
    chart, white = samples / "ColorCard_Big.JPG", samples / "Blank.JPG"
    scenes = [samples / "Sample_3572.JPG", samples / "Sample_3573.JPG"]
    for path in [chart, white, *scenes, reference_csv]:
        if not path.is_file():
            raise FileNotFoundError(path)
    module_path = Path(colorcorrect.__file__).resolve()
    if not module_path.is_relative_to(Path(sys.prefix).resolve()):
        raise RuntimeError(f"Install the SDK in this Python environment; imported {module_path}")
    # Config stage switches take precedence over nested options.
    no_gc = replace(config, do_gc=False)
    assert no_gc.calibration_options().do_gc is False
    for choice in alternative_configs(config).values():
        choice.calibration_options().to_dict()
    reference = ReferenceProfile.from_csv(reference_csv, chart="classic", color_space="lab",
                                         white_point="d50", order="row-major")
    with ColorCorrection(config, root=root, timeout=900) as cc:
        worker = cc.client.engine.executable.resolve()
        assert worker.is_relative_to(root.resolve()), worker
        detected = cc.client.detect(chart)
        # This chart fixture contains a single Classic face. Explicit selection in
        # Client is preferable for a custom profile when the photo has many faces.
        fitted = cc.run(Image=chart, White_Image=white, detection=detected, reference=reference)
        metrics = fitted.metrics
        assert metrics and metrics.get("do_ffc") is True, fitted.warnings
        assert metrics.get("reference_context"), "Custom references did not reach the fit"
        assert fitted.corrected_image and fitted.corrected_image.is_file()
        batch = cc.predict_images(images=scenes, options=ApplyOptions(output_format="jpg"))
        assert len(batch.outputs) == 2 and all(path.is_file() for path in batch.outputs)
        # One failed input is preserved explicitly alongside a successful outcome.
        partial = cc.predict_images(images=[scenes[0], out / "missing.jpg"], on_error="continue",
                                    options=ApplyOptions(output_format="jpg"))
        assert len(partial.outputs) == 1 and len(partial.failures) == 1
        saved = fitted.path
        fit_backends = {"actual": fitted.backends.actual, "diagnostics": fitted.backends.diagnostics}
        batch_backends = {"actual": batch.backends.actual, "diagnostics": batch.backends.diagnostics}
    with ColorCorrection(Config(save_path=out / "reload"), root=root, timeout=900) as cc:
        loaded = cc.load_model(saved)
        reapplied = cc.predict_image(scenes[0], options=ApplyOptions(output_format="jpg"))
        assert reapplied.images[0].ok and not reapplied.warnings, reapplied.warnings
        ablation = cc.predict_image(scenes[0], options=ApplyOptions(do_wb=False, write_output=False))
        assert ablation.images[0].ok and not ablation.outputs
        lut = loaded.export_lut(out / "correction.cube", size=33)
        assert lut.path.is_file()
    result = {"ok": True, "interpreter": sys.executable, "sdk_module": str(module_path),
              "worker": str(worker), "output": str(out), "model": str(saved),
              "metrics": metrics, "fit_backends": fit_backends, "batch_backends": batch_backends,
              "reload_backends": {"actual": reapplied.backends.actual, "diagnostics": reapplied.backends.diagnostics},
              "ablation_backends": {"actual": ablation.backends.actual, "diagnostics": ablation.backends.diagnostics},
              "partial_outcomes": [{"image": str(row.image), "ok": row.ok, "error": row.error} for row in partial.images],
              "outputs": [str(path) for path in batch.outputs], "lut": str(lut.path)}
    (out / "verification.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2), flush=True)
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--samples", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--reference-csv", type=Path, required=True)
    args = parser.parse_args()
    run(args.root.resolve(), args.samples.resolve(), args.output_dir.resolve(), args.reference_csv.resolve())


if __name__ == "__main__":
    main()
