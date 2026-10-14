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
        # A chart image alone is not a fitting handshake; fail before writing artifacts.
        try:
            engine.fit(samples / "ColorCard_Big.JPG", output=out / "no-chart.png",
                report=out / "no-chart.report.json", model_out=out / "no-chart.model.json")
        except EngineError as error:
            assert "run requires handshake or chart_json" in error.reply["error"], error.reply
        else:
            raise AssertionError("Missing chart handshake was accepted")
        assert not (out / "no-chart.png").exists()
        bad_white = out / "invalid-white.png"
        bad_white.write_text("not an image", encoding="utf-8")
        for index, white in enumerate((out / "missing-white.png", bad_white)):
            try:
                engine.fit(samples / "ColorCard_Big.JPG", handshake=detections["ColorCard_Big.JPG"]["handshake"],
                    output=out / f"bad-ffc-{index}.png", report=out / f"bad-ffc-{index}.report.json",
                    model_out=out / f"bad-ffc-{index}.model.json", do_ffc=True, white=white,
                    phase2_teacher=False)
            except EngineError as error:
                assert "FFC reference could not be loaded or fitted" in error.reply["error"], error.reply
            else:
                raise AssertionError("Unreadable FFC reference silently skipped in fit")
            assert not (out / f"bad-ffc-{index}.png").exists()
            reply = engine.apply(out / "model.json", [samples / "Sample_3572.JPG", samples / "Sample_3573.JPG"],
                out / f"bad-apply-{index}", do_ffc=True, white=white, check=False)
            assert reply["ok"] is False and len(reply["results"]) == 2, reply
            assert all(row["ok"] is False and "FFC reference could not be loaded or fitted" in row["error"]
                for row in reply["results"]), reply
            assert not list((out / f"bad-apply-{index}").glob("*_corrected.*"))
        # A supplied but disabled white reference remains unused.
        ignored_white = engine.apply(out / "model.json", samples / "Sample_3572.JPG", out / "ffc-off",
            do_ffc=False, white=bad_white, write_output=False)
        assert ignored_white["results"][0]["ok"] is True
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
