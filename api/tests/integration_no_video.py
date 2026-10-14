"""Verify the retired video protocol/SDK surface without media tools or inputs."""
import argparse
import json
import os
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from colorcorrect import Engine, EngineError, VIDEO_UNSUPPORTED


def validate_reply(reply):
    assert reply["ok"] is False, reply
    assert reply["code"] == "unsupported_feature", reply
    assert reply["feature"] == "video", reply
    assert reply["error"] == VIDEO_UNSUPPORTED, reply


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--engine", type=Path)
    args = parser.parse_args()
    operations = []
    with tempfile.TemporaryDirectory(prefix="colorcorrect-no-video-") as temporary:
        folder = Path(temporary)
        # Bogus explicit paths prove removed commands never attempt sidecar lookup.
        with patch.dict(os.environ, {"CCP_FFMPEG": str(folder / "missing-ffmpeg"),
                                     "CCP_FFPROBE": str(folder / "missing-ffprobe")}):
            with Engine(args.engine, root=args.root, policy="cpu", timeout=60) as engine:
                # Bypass only the SDK's local compatibility stub to exercise the
                # actual native NDJSON request/reply protocol on its real pipe.
                for command in ("video", "video_grade", "VIDEO", "VIDEO_GRADE"):
                    request = {"cmd": command, "input": str(folder / "missing.mp4"),
                               "output": str(folder / "unused.mp4"),
                               "model": str(folder / "missing.json"),
                               "do_ffc": True, "white": str(folder / "missing-white.jpg")}
                    engine._process.stdin.write(json.dumps(request) + "\n")
                    engine._process.stdin.flush()
                    validate_reply(engine._read_reply(60))
                assert engine.devices()["ok"] is True, "rejection must leave the worker usable"
                assert list(folder.iterdir()) == [], "unsupported video must not create artifacts"
                operations.append("native retired video commands fail without FFmpeg or artifacts")
            # A local stub must not need even a worker process to return the error.
            engine = Engine(args.engine, root=args.root, policy="cpu")
            for operation in (lambda: engine.video("missing.mp4", "unused.mp4"),
                              lambda: engine.video_grade("missing.json", "missing.mp4", "unused.mp4")):
                try:
                    operation()
                except EngineError as error:
                    validate_reply(error.reply)
                else:
                    raise AssertionError("Retired SDK method unexpectedly succeeded")
            assert engine._process is None
            validate_reply(engine.request("video_grade", check=False))
            engine.close()
            operations.append("Python legacy helpers fail locally with explicit compatibility error")
    print(json.dumps({"ok": True, "operations": operations}, indent=2))


if __name__ == "__main__":
    main()
