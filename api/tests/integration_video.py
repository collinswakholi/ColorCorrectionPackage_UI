"""Real SDR video API smoke test; synthesizes a short clip from the chart fixture."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import Engine


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--samples", type=Path)
    parser.add_argument("--engine", type=Path)
    args = parser.parse_args()
    samples = (args.samples or Path(os.environ.get("COLORCORRECT_SAMPLES", str(args.root / "test_images")))).resolve()
    suffix = ".exe" if os.name == "nt" else ""
    ffmpeg = next((p for p in [Path(os.environ.get("CCP_FFMPEG", "")), args.root / ("ffmpeg" + suffix), args.root / "installer" / ("ffmpeg" + suffix), Path(shutil.which("ffmpeg") or "")] if p.is_file()), None)
    assert ffmpeg, "FFmpeg is required for this opt-in test"
    out = Path(tempfile.mkdtemp(prefix="colorcorrect-video-api-"))
    print("Artifacts:", out, flush=True)
    subprocess.run([str(ffmpeg), "-hide_banner", "-loglevel", "error", "-loop", "1", "-i", str(samples / "ColorCard_Big.JPG"),
        "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-t", "1", "-r", "2", "-vf", "scale=1920:-2",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
        "-c:a", "aac", str(out / "chart.mp4")], check=True, timeout=60,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    with Engine(args.engine, root=args.root, timeout=180, progress=lambda m: print(m, flush=True)) as engine:
        fitted = engine.video(out / "chart.mp4", out / "calibrated.mp4", keyframe_sec=0.0,
            model="rpcc2", phase2_teacher=False)
        assert Path(fitted["output"]).is_file() and Path(fitted["model_out"]).is_file()
        identity = engine.video_grade(fitted["model_out"], out / "chart.mp4", out / "identity.mp4",
            cube_out=out / "identity.cube", do_gc=False, do_wb=False, do_cc=False)
        assert Path(identity["output"]).is_file()
        data = [list(map(float, line.split())) for line in (out / "identity.cube").read_text().splitlines()
                if line.strip() and line[0].isdigit()]
        assert len(data) == 33 ** 3
        for index, actual in enumerate(data):
            expected = [(index % 33) / 32, ((index // 33) % 33) / 32, (index // 1089) / 32]
            assert max(abs(a - b) for a, b in zip(actual, expected)) < 1e-7, "disabled stages must export identity"
        ffc = engine.video_grade(fitted["model_out"], out / "chart.mp4", out / "ffc.mp4",
            do_ffc=True, white=samples / "Blank.JPG")
        assert Path(ffc["output"]).is_file() and ffc["ffc"] is True
    print(json.dumps({"ok": True, "operations": ["video fit/grade", "video_grade identity switches", "video_grade per-frame FFC"], "artifacts": str(out)}, indent=2))


if __name__ == "__main__":
    main()
