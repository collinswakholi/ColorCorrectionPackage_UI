"""Installed SDK full workflow; runs outside the checkout without GPU requirements."""
from pathlib import Path
import argparse
import importlib.metadata
import json
import os
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--fixtures", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--backend", choices=("auto", "cpu-inference"), required=True)
    args = parser.parse_args()
    package, fixtures = args.package.resolve(strict=True), args.fixtures.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    os.chdir(output)
    if args.backend == "cpu-inference":
        os.environ["CCP_INFERENCE_PROFILE"] = str(output / "cpu-inference-profile")
    import colorcorrect
    from colorcorrect import ColorCorrection, Config, BatchProcessingError, CalibrationOptions
    module = Path(colorcorrect.__file__).resolve()
    if not module.is_relative_to(Path(sys.prefix).resolve()):
        raise RuntimeError("SDK import escaped installed virtual environment: " + str(module))
    report = {"backend_requested": args.backend, "module": str(module), "package": str(package),
              "sdk_version": importlib.metadata.version("colorcorrect-studio"), "checks": []}

    def record(name):
        report["checks"].append(name)
        (output / "result.json").write_text(json.dumps(report, indent=2, default=str), encoding="utf-8")
        print("PASS " + name, flush=True)

    try:
        with ColorCorrection(Config(save_path=output / "results Ω", options=CalibrationOptions(cross_validate=False)),
                             root=package, backend=args.backend) as cc:
            engine = cc.client.engine
            if not engine.executable.resolve().is_relative_to(package):
                raise RuntimeError("Worker escaped installed package")
            report["ready"] = engine.ready
            report["devices"] = engine.request("devices")
            worker_process = engine._process
            detected = cc.client.detect(fixtures / "chart.jpg")
            assert detected.charts, "Chart fixture was not detected"
            actual = detected.backends.actual.get("detect", "")
            if args.backend == "cpu-inference":
                assert actual == "cpu", detected.backends
            else:
                assert actual, "Auto did not report actual detection backend"
            report["detection"] = {"actual": detected.backends.actual, "diagnostics": detected.backends.diagnostics}
            record("installed chart detection reports actual engine")
            model = cc.run(fixtures / "chart.jpg", detection=detected)
            assert model.path.is_file() and model.corrected_image.is_file()
            assert model.metrics
            record("fit and correction without FFC")
            no_chart = engine.request("detect", image=fixtures / "blank.jpg", check=False)
            assert no_chart.get("n_charts") == 0, no_chart
            empty_reason = str(no_chart.get("error", "")).lower()
            assert "no chart" in empty_reason or "no lattice" in empty_reason or no_chart.get("ok"), no_chart
            record("no-chart input is distinguished from inference failure")
            ffc = cc.run(fixtures / "chart.jpg", White_Image=fixtures / "blank.jpg", detection=detected)
            assert ffc.path.is_file() and ffc.corrected_image.is_file()
            assert ffc.metrics["do_ffc"] is True
            report["ffc_backends"] = {"actual": ffc.backends.actual, "diagnostics": ffc.backends.diagnostics}
            record("automatic white-plane detection and FFC fit")
            applied = cc.predict_images([fixtures / "sample.jpg", fixtures / "chart.jpg"])
            assert len(applied.outputs) == 2 and not applied.failures
            assert all(path.is_file() for path in applied.outputs)
            record("FFC batch application preserves both outputs")
            partial = cc.predict_images([fixtures / "sample.jpg", fixtures / "missing.jpg"], on_error="continue")
            assert len(partial.outputs) == 1 and len(partial.failures) == 1
            try:
                cc.predict_images([fixtures / "sample.jpg", fixtures / "missing.jpg"])
                raise AssertionError("Default batch failure did not raise")
            except BatchProcessingError as error:
                assert len(error.result.outputs) == 1 and len(error.result.failures) == 1
            assert cc.predict_image(fixtures / "sample.jpg").outputs[0].is_file()
            record("partial failures retain successful outputs and subsequent recovery")
        assert worker_process.poll() is not None, "Closed SDK left its worker running"
        assert worker_process.returncode == 0, f"Worker did not exit cleanly: {worker_process.returncode}"
        report["worker_exit_code"] = worker_process.returncode
        record("SDK closes its worker cleanly")
        with ColorCorrection(Config(save_path=output / "reloaded"), root=package, backend=args.backend) as cc:
            cc.load_model(ffc.path)
            assert cc.predict_image(fixtures / "sample.jpg").outputs[0].is_file()
            record("FFC model reload and application in a new worker")
        if args.backend == "cpu-inference":
            profiles = list(output.glob("cpu-inference-profile*.json"))
            providers = {event.get("args", {}).get("provider") for path in profiles
                         for event in json.loads(path.read_text(encoding="utf-8"))
                         if event.get("cat") == "Node" and event.get("args", {}).get("provider")}
            assert providers == {"CPUExecutionProvider"}, providers
            report["node_providers"] = sorted(providers)
            report["profiles"] = [str(path) for path in profiles]
            record("ORT profile confirms explicit CPU node placement")
        report["status"] = "passed"
    except Exception as error:
        report.update(status="failed", error=repr(error))
        raise
    finally:
        (output / "result.json").write_text(json.dumps(report, indent=2, default=str), encoding="utf-8")


if __name__ == "__main__":
    main()
