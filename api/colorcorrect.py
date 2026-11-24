"""ColorCorrect Studio's dependency-free, local Python API (Python 3.10+).

Use Client for typed workflows or Engine for the full NDJSON protocol. Both use
the desktop app's native worker without a GUI, server, or Python ML stack.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

from colorcorrect_engine import SUPPORTED_PROTOCOL_VERSIONS, Calibration, Engine, EngineError
from colorcorrect_sdk import (
    Client, CalibrationModel, ReferenceProfile, DetectionOptions, CalibrationOptions,
    ApplyOptions, FFCOptions, NeuralNetworkOptions, DetectionResult, DetectedChart,
    BackendInfo, ImageResult, ApplyResult, LUTResult, ColorCorrectError,
    InvalidInputError, MissingRuntimeError, UnsupportedCapabilityError,
    ProcessingError, ProcessingTimeoutError, ClosedClientError, BatchProcessingError,
)
from colorcorrect_pipeline import ColorCorrection, Config

__version__ = "3.0.0"
COMMANDS = ("devices", "setup_probe", "warmup", "detect", "run", "apply", "preview_stage",
            "export_cube")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=COMMANDS)
    parser.add_argument("--json", default="{}", help="JSON parameters, @file.json, or - for stdin")
    parser.add_argument("--engine", help="Path to ccp-engine executable")
    parser.add_argument("--root", help="App resource folder with models/ and refs/")
    parser.add_argument("--policy", default="auto", help="auto (GPU then CPU inference), cpu-inference, directml, webgpu, vulkan; cpu selects CPU LUTs with Auto inference")
    parser.add_argument("--timeout", type=float, default=600)
    args = parser.parse_args(argv)
    try:
        source = sys.stdin.read() if args.json == "-" else Path(args.json[1:]).read_text(encoding="utf-8-sig") if args.json.startswith("@") else args.json
        parameters = json.loads(source)
        if not isinstance(parameters, dict):
            raise ValueError("--json must contain an object")
        with Engine(args.engine, root=args.root, policy=args.policy, timeout=args.timeout,
                    progress=lambda msg: print(msg, file=sys.stderr)) as engine:
            reply = engine.request(args.command, **parameters)
        print(json.dumps(reply, ensure_ascii=False, indent=2))
        return 0
    except (EngineError, OSError, ValueError, TypeError, TimeoutError) as exc:
        print(json.dumps(exc.reply if isinstance(exc, EngineError) and exc.reply else {"ok": False, "error": str(exc)}), file=sys.stdout)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
