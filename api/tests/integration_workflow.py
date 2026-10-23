"""Opt-in native integration test. Never mutates the source sample corpus."""
from pathlib import Path
import argparse
import json
import os
import sys
import tempfile
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import Engine, EngineError


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--samples", type=Path)
    parser.add_argument("--engine", type=Path)
    args = parser.parse_args()
    samples = (args.samples or Path(os.environ.get("COLORCORRECT_SAMPLES", str(args.root / "test_images")))).resolve()
    missing = [name for name in ("ColorCard_Big.JPG", "ColorCard_Combo.JPG", "Blank.JPG", "Sample_3572.JPG", "Sample_3573.JPG")
               if not (samples / name).is_file()]
    if missing:
        parser.error(f"Required public fixtures are missing from {samples}: {', '.join(missing)}")
    out = Path(tempfile.mkdtemp(prefix="colorcorrect-api-test-"))
    print("Artifacts:", out, flush=True)
    timings = {}
    with Engine(args.engine, root=args.root, progress=lambda m: print(m, flush=True)) as engine:
        for operation in (engine.devices, engine.warmup):
            t = time.perf_counter()
            assert operation()["ok"]
            timings[operation.__name__] = round(time.perf_counter() - t, 3)
        detections = {}
        for name in ("ColorCard_Big.JPG", "ColorCard_Combo.JPG"):
            t = time.perf_counter()
            detections[name] = engine.detect(samples / name, handshake_out=out / (name + ".json"))
            assert detections[name]["n_charts"] >= 1
            timings["detect_" + name] = round(time.perf_counter() - t, 3)
        t = time.perf_counter()
        fit = engine.fit(samples / "ColorCard_Big.JPG", handshake=detections["ColorCard_Big.JPG"]["handshake"],
            output=out / "chart.png", report=out / "report.json", model_out=out / "model.json",
            model="rpcc2", do_ffc=True, white=samples / "Blank.JPG", ffc={"method": "pls", "degree": 5},
            do_gc=True, do_wb=True, do_cc=True, cross_validate=True, phase2_teacher=False)
        timings["fit"] = round(time.perf_counter() - t, 3)
        assert fit["report_json"]["do_ffc"] is True
        assert "validation_delta_e_2000" in fit["report_json"]
        for artifact in ("output", "report", "model_out"):
            assert Path(fit[artifact]).is_file()
        t = time.perf_counter()
        applied = engine.apply(out / "model.json", [samples / "Sample_3572.JPG", samples / "Sample_3573.JPG"],
            out / "applied", do_ffc=True, white=samples / "Blank.JPG", output_format="png")
        assert all(Path(row["output"]).is_file() and not row.get("ffc_skipped") for row in applied["results"])
        timings["apply_2_images"] = round(time.perf_counter() - t, 3)
        for stage in ("ffc", "gc", "wb", "cc"):
            assert engine.preview(out / "model.json", samples / "ColorCard_Big.JPG", stage,
                out / "previews" / stage, do_ffc=True, white=samples / "Blank.JPG")["ok"]
        # This must fail, rather than silently exporting the preceding fit.
        try:
            engine.export_cube(out / "missing-model.json", out / "wrong.cube")
        except EngineError:
            pass
        else:
            raise AssertionError("Explicit model ignored in export_cube")
        assert not (out / "wrong.cube").exists()
    for stage, filename in (("ffc", "ffc.png"), ("gc", "gc.png"), ("wb", "wb.png"), ("cc", "corrected.png")):
        assert (out / "previews" / stage / filename).stat().st_size > 100, "graceful shutdown must complete deferred PNGs"
    # Reload from disk in an independent session, with no last-fit state.
    with Engine(args.engine, root=args.root) as engine:
        cube = engine.export_cube(out / "model.json", out / "grade.cube")
        assert Path(cube["path"]).is_file()
        try:
            engine.apply(out / "model.json", [samples / "Sample_3572.JPG", out / "missing.jpg"],
                out / "partial", write_output=False)
        except EngineError as exc:
            assert exc.reply["results"][0]["ok"] is True
            assert exc.reply["results"][1]["ok"] is False
        else:
            raise AssertionError("Partial failure was not raised")
    print(json.dumps({"ok": True, "timings_seconds": timings,
        "chart_types": {name: result["chart_type"] for name, result in detections.items()},
        "delta_e_2000": fit["report_json"]["delta_e_2000"],
        "validation": fit["report_json"]["validation_delta_e_2000"]}, indent=2))


if __name__ == "__main__":
    main()
