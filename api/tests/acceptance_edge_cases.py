"""Installed guest-only acceptance for CV, FFC controls and rejection/recovery.

Run with a clean installed wheel in Sandbox/WSL. Requires small/chart.jpg,
small/white.png, small/uniform.png, small/manifest.json and identity-model.json.
Only Python stdlib and the installed SDK are used; never inject a source path.
"""
from __future__ import annotations
import argparse
import binascii
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import struct
import sys
import time
import traceback
import zlib


def finite(value):
    if isinstance(value, float):
        assert math.isfinite(value), "nonfinite result"
    elif isinstance(value, dict):
        for item in value.values():
            finite(item)
    elif isinstance(value, list):
        for item in value:
            finite(item)


def png_rgb(path):
    """Read only the deliberately controlled, noninterlaced RGB8 PNG fixtures."""
    data = path.read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    offset = 8; payload = bytearray(); dimensions = None
    while offset < len(data):
        size = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        value = data[offset + 8:offset + 8 + size]
        crc = struct.unpack_from(">I", data, offset + 8 + size)[0]
        assert binascii.crc32(kind + value) & 0xffffffff == crc
        if kind == b"IHDR":
            width, height, depth, color, compression, filtering, interlace = struct.unpack(">IIBBBBB", value)
            assert (depth, color, compression, filtering, interlace) == (8, 2, 0, 0, 0)
            dimensions = (width, height)
        elif kind == b"IDAT":
            payload.extend(value)
        elif kind == b"IEND":
            break
        offset += size + 12
    assert dimensions
    width, height = dimensions; stride = width * 3
    raw = zlib.decompress(payload)
    assert len(raw) == height * (stride + 1)
    output = bytearray(); previous = bytearray(stride)
    for y in range(height):
        start = y * (stride + 1); mode = raw[start]
        assert mode in range(5)
        row = bytearray(raw[start + 1:start + 1 + stride])
        for index in range(stride):
            left = row[index - 3] if index >= 3 else 0
            up = previous[index]
            upper_left = previous[index - 3] if index >= 3 else 0
            if mode == 1:
                predictor = left
            elif mode == 2:
                predictor = up
            elif mode == 3:
                predictor = (left + up) // 2
            elif mode == 4:
                guess = left + up - upper_left
                distances = (abs(guess-left), abs(guess-up), abs(guess-upper_left))
                predictor = (left, up, upper_left)[distances.index(min(distances))]
            else:
                predictor = 0
            row[index] = (row[index] + predictor) & 255
        output.extend(row); previous = row
    return width, height, bytes(output)


def preview_rgb(path):
    raw = path.read_bytes()
    assert raw[:8] == b"CCPRGB8\n"
    width, height = struct.unpack_from("<II", raw, 8)
    assert len(raw) == 16 + width * height * 3
    return width, height, raw[16:]


def cv(pixels):
    # The fixtures are neutral; inspect all three channels to catch color casts.
    values = pixels
    mean = statistics.mean(values)
    assert mean > 0
    return statistics.pstdev(values) / mean


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("package", "fixtures", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    args.package = args.package.resolve(strict=True)
    args.fixtures = args.fixtures.resolve(strict=True)
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    os.chdir(args.output)
    import colorcorrect
    from colorcorrect import Client, CalibrationOptions, FFCOptions, NeuralNetworkOptions
    assert sys.prefix != sys.base_prefix, "Use a fresh installed-wheel virtual environment"
    assert Path(colorcorrect.__file__).resolve().is_relative_to(Path(sys.prefix).resolve())
    report = {"status": "running", "module": colorcorrect.__file__, "checks": [], "scope": "installed guest app/API"}
    def record(name, **evidence):
        report["checks"].append({"check": name, **evidence})
        save()
        print("PASS", name, flush=True)
    def save():
        (args.output / "edge-cases.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    try:
        small = args.fixtures / "small"
        dimensions = tuple(json.loads((small / "manifest.json").read_text())["dimensions"])
        width, height = dimensions; crop = f"0,0,{width},{height}"
        identity = args.fixtures / "identity-model.json"
        assert identity.is_file()
        with Client(root=args.package, backend="auto", timeout=600) as client:
            assert client.engine.executable.parent == args.package
            detection = client.detect(small / "chart.jpg")
            for method in ("rpcc3", "nn"):
                started = time.perf_counter()
                options = CalibrationOptions(model=method, do_ffc=True, crop_rect=crop,
                    ffc=FFCOptions(method="poly", bins=12, degree=3),
                    nn=NeuralNetworkOptions(hidden_layers=(64, 32), epochs=200, patience=50),
                    cross_validate=True, phase2_teacher=False)
                fitted = client.calibrate(small / "chart.jpg", detection=detection, white=small / "white.png",
                                          output_dir=args.output, options=options)
                metrics = fitted.metrics; finite(metrics)
                assert all(metrics[key] for key in ("do_ffc", "do_gc", "do_wb", "do_cc"))
                assert metrics["validation_label"] == "CC-only"
                assert metrics["validation_delta_e_2000"]["mean"] >= 0
                saved = json.loads(fitted.path.read_text()); finite(saved)
                if method == "nn":
                    assert saved["correction"]["hidden_layers"] == [64, 32]
                restored = client.load_calibration(fitted.path)
                assert restored.metrics == metrics
                good = restored.apply(small / "chart.jpg", output_dir=args.output)
                assert len(good.outputs) == 1 and not good.failures
                pid = client.engine._process.pid
                invalid = args.output / f"missing-ffc-{method}"
                failure = client.engine.request("apply", check=False, model=fitted.path,
                    images=[small / "chart.jpg"], output_dir=invalid, do_ffc=True,
                    white=args.output / "missing-white.png", fast_mode=True, lut_device="cpu")
                assert failure.get("ok") is False
                assert failure["results"][0]["ok"] is False
                assert "FFC reference could not be loaded or fitted" in failure["results"][0]["error"]
                assert not invalid.exists() or not any(p.is_file() for p in invalid.rglob("*")), "failure wrote an image/artifact"
                recovered = restored.apply(small / "chart.jpg", output_dir=args.output)
                assert len(recovered.outputs) == 1 and not recovered.failures
                assert client.engine._process.pid == pid, "worker restarted instead of recovering"
                record(f"{method}: CV, reloadable multilayer fit and native FFC failure/recovery",
                       seconds=time.perf_counter()-started, validation=metrics["validation_delta_e_2000"],
                       model_sha256=hashlib.sha256(fitted.path.read_bytes()).hexdigest(),
                       missing_ffc_error=failure["results"][0]["error"])

            for filename in ("uniform.png", "white.png"):
                image = small / filename
                source = png_rgb(image)
                assert source[:2] == dimensions
                assert source[2][0::3] == source[2][1::3] == source[2][2::3], "control must be neutral RGB"
                folder = args.output / ("ffc-control-" + image.stem)
                result = client.engine.request("preview_stage", model=identity, image=image, stage="ffc",
                    preview_dir=folder, white=image, do_ffc=True, do_gc=False, do_wb=False, do_cc=False,
                    crop_rect=crop, ffc=FFCOptions(method="poly", bins=12, degree=3).to_dict(), lut_device="cpu")
                assert not result.get("ffc_skipped")
                corrected = preview_rgb(folder / "ffc.rgb")
                assert corrected[:2] == dimensions
                before, after = cv(source[2]), cv(corrected[2])
                if filename == "uniform.png":
                    assert before == 0
                    difference = max(abs(a-b) for a,b in zip(source[2], corrected[2]))
                    assert difference <= 1, difference
                    record("uniform FFC identity", max_encoded_difference=difference)
                else:
                    assert before > 0.01, "spatial gradient control is too weak"
                    assert after <= before * 0.25, (before, after)
                    assert min(corrected[2]) > 0 and max(corrected[2]) < 255, "control clipped"
                    record("spatial FFC reduces controlled illumination variation", before_cv=before, after_cv=after,
                           scope="relative spatial uniformity; not independent physical color truth")

            commands = ("video", "VIDEO", "Video", "vIdEo", "video_grade", "VIDEO_GRADE", "Video_Grade", "vIdEo_GrAdE")
            pid = client.engine._process.pid
            for index, command in enumerate(commands):
                folder = args.output / f"removed-video-{index}"
                output = folder / "should-not-exist.mp4"
                reply = client.engine.request(command, check=False, model=identity, input=small / "uniform.png",
                                              output=output, output_dir=folder)
                assert reply.get("ok") is False and "removed" in reply.get("error", "").lower()
                assert not folder.exists() or not any(p.is_file() for p in folder.rglob("*"))
                assert client.engine._process.pid == pid
            recovery = args.output / "after-video"
            client.engine.request("preview_stage", model=identity, image=small / "uniform.png", stage="cc",
                                  preview_dir=recovery, fast_mode=True, lut_device="cpu")
            assert preview_rgb(recovery / "corrected.rgb")[:2] == dimensions
            assert client.engine._process.pid == pid
            record("all removed video command case variants reject without artifacts and recover", commands=list(commands))
        report["status"] = "passed"
    except BaseException:
        report["status"] = "failed"; report["error"] = traceback.format_exc()
        raise
    finally:
        save()


if __name__ == "__main__":
    main()
