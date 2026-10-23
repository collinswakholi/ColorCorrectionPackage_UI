"""Final-package acceptance only. Run with a freshly installed wheel, outside source.

No sys.path injection: this must exercise the installed SDK and extracted runtime.
Requires a fixture folder with chart.jpg, sample.jpg, blank.jpg and classic.csv.
"""
import argparse
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import sys
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--fixtures", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    os.chdir(args.output)
    from colorcorrect import (Client, ColorCorrection, Config, ReferenceProfile, CalibrationOptions,
                              BatchProcessingError, ClosedClientError, InvalidInputError)
    import colorcorrect
    report = {"platform":platform.platform(),"machine":platform.machine(),"python":sys.version,
              "sdk_version":importlib.metadata.version("colorcorrect-studio"),
              "module_path":colorcorrect.__file__,"checks":[],"scope":"final installed SDK and packaged worker"}
    started = time.monotonic()
    def passed(name):
        report["checks"].append(name)
        (args.output / "acceptance.json").write_text(json.dumps(report,indent=2),encoding="utf8")
        print("PASS",name,flush=True)
    fixture = args.fixtures.resolve()
    try:
        with ColorCorrection(Config(save_path=args.output / "résults Ω"), root=args.package) as cc:
            fitted = cc.run(Image=fixture / "chart.jpg")
            assert fitted.metrics and fitted.path.is_file() and fitted.corrected_image.is_file()
            assert fitted.backends.requested == "auto"
            assert fitted.backends.actual.get("detect", "").split(":", 1)[0] in ("directml", "webgpu", "vulkan")
            passed("familiar run() detects, fits and writes real artifacts")
            result = cc.predict_image(fixture / "sample.jpg")
            assert len(result.outputs)==1 and result.outputs[0].is_file()
            passed("predict_image() from another working directory with Unicode output")
            detection = cc.client.detect(fixture / "chart.jpg")
            assert len(detection.charts)==1
            reference = ReferenceProfile.from_csv(fixture / "classic.csv", chart="classic")
            custom = cc.client.calibrate(fixture / "chart.jpg", reference=reference, detection=detection,
                                        output_dir=args.output, options=CalibrationOptions(write_output=False))
            assert custom.metrics["reference_context"]
            passed("custom CSV references and saved provenance")
            try:
                cc.predict_images([fixture / "sample.jpg", fixture / "missing.jpg"])
                raise AssertionError("batch failure was hidden")
            except BatchProcessingError as error:
                assert len(error.result.outputs)==1 and len(error.result.failures)==1
            passed("batch partial failure retains successful output")
            lut=fitted.export_lut(args.output / "correction.cube",size=17)
            assert lut.path.is_file()
            try:
                fitted.export_lut(lut.path)
                raise AssertionError("LUT overwritten")
            except InvalidInputError: pass
            passed("LUT export and overwrite protection")
            for command in ("video","video_grade"):
                reply=cc.client.engine.request(command, check=False)
                assert reply["ok"] is False and "removed" in reply["error"]
            passed("removed video commands explicitly rejected")
        try:
            fitted.apply(fixture / "sample.jpg",output_dir=args.output)
            raise AssertionError("closed client restarted")
        except ClosedClientError: pass
        passed("closed-client model does not restart")
        with ColorCorrection(Config(save_path=args.output),root=args.package) as cc:
            restored=cc.load_model(fitted.path)
            assert restored.metrics==fitted.metrics
            assert cc.predict_image(fixture / "sample.jpg").outputs[0].is_file()
            passed("saved model reload and apply in a new worker")
            ffc=cc.run(fixture / "chart.jpg",White_Image=fixture / "blank.jpg")
            assert ffc.metrics["do_ffc"] is True
            assert cc.predict_image(fixture / "sample.jpg").outputs[0].is_file()
            passed("FFC calibration and reuse")
        with Client(root=args.package, backend="auto") as client:
            restored=client.load_calibration(fitted.path)
            automatic=restored.apply(fixture / "sample.jpg", output_dir=args.output)
            assert automatic.outputs[0].is_file()
            backend=automatic.backends.actual.get("apply", "")
            assert backend == "cpu" or backend.startswith("wgpu:"), backend
            report["automatic_lut"] = {"backend":backend,"diagnostics":automatic.backends.diagnostics}
            passed("automatic portable LUT application with actual execution report")
        report["status"]="passed"
    except Exception as error:
        report.update(status="failed",error=repr(error))
        raise
    finally:
        report["elapsed_seconds"]=time.monotonic()-started
        (args.output / "acceptance.json").write_text(json.dumps(report,indent=2),encoding="utf8")

if __name__=="__main__": main()
