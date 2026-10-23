"""ColorCorrect Studio's dependency-free, local Python API (Python 3.10+).

Use Client for typed workflows or Engine for the full NDJSON protocol. Both use
the desktop app's native worker without a GUI, server, or Python ML stack.
"""
from __future__ import annotations

import argparse
import copy
from collections import deque
import json
import math
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import tempfile
from typing import Any, Callable

__version__ = "2.2.1"
COMMANDS = ("devices", "warmup", "detect", "run", "apply", "preview_stage",
            "export_cube")


class EngineError(RuntimeError):
    """Failed request; reply retains per-image results for partial batches."""
    def __init__(self, message: str, reply: dict | None = None):
        super().__init__(message)
        self.reply = reply


def _root(start: Path) -> Path | None:
    for candidate in (start, *start.parents):
        if (candidate / "refs/neutrals.json").is_file() and (candidate / "models/opt2").is_dir():
            return candidate
    return None


def _json_default(value: Any) -> str:
    if isinstance(value, os.PathLike):
        return os.fspath(value)
    raise TypeError(f"Not JSON serializable: {type(value).__name__}")


class Engine:
    """Serialized NDJSON worker client. Relative paths use the caller's cwd.

    Explicit executable/root override environment and discovery. Progress runs
    synchronously on the calling thread; callbacks must not issue requests.
    A timeout/protocol error kills the worker; the next request starts a fresh
    session, so persist model paths rather than relying on last-fit state.
    """
    def __init__(self, executable: str | Path | None = None, *,
                 root: str | Path | None = None, policy: str = "cpu",
                 timeout: float = 600, progress: Callable[[str], None] | None = None):
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("timeout must be finite and positive")
        self.timeout, self.policy, self.progress = timeout, policy, progress
        self._explicit_root = root is not None
        # An explicit package root must not borrow a worker from the developer's
        # shell. An explicit executable remains the protocol escape hatch.
        exe = executable or (None if root is not None else
                             os.environ.get("CCP_ENGINE_EXE") or os.environ.get("COLORCORRECT_EXE"))
        located = Path(root or os.environ.get("CCP_NEW_UI_ROOT", "")).resolve() if root or os.environ.get("CCP_NEW_UI_ROOT") else None
        self.root = located or (_root(Path(exe).resolve().parent) if exe else None) or _root(Path(__file__).resolve().parent) or _root(Path.cwd())
        if self.root is None or not (self.root / "refs/neutrals.json").is_file():
            raise FileNotFoundError("Set root= or CCP_NEW_UI_ROOT to the app folder containing models/ and refs/.")
        if exe:
            self.executable = Path(exe).resolve()
        else:
            name = "ccp-engine.exe" if os.name == "nt" else "ccp-engine"
            candidates = ([self.root / "ccp-engine-launch"] if sys.platform.startswith("linux") else [])
            candidates += [self.root / name, self.root / "engine/target/release" / name,
                          self.root / "dist/win-cpu" / name]
            for target in ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64", "macos-arm64"):
                candidates.extend((self.root / "dist" / target / "payload" / name,
                                   self.root / "dist" / target / name))
            self.executable = next((p for p in candidates if p.is_file()), candidates[0])
        if not self.executable.is_file():
            raise FileNotFoundError(f"Worker not found: {self.executable}. Use an extracted app package or build the engine.")
        self._process: subprocess.Popen | None = None
        self._lines: queue.Queue = queue.Queue()
        self._stderr: deque[str] = deque(maxlen=80)
        self._readers: list[threading.Thread] = []
        self._lock = threading.Lock()
        self.ready: dict = {}

    def _environment(self) -> dict[str, str]:
        env = dict(os.environ)
        env["CCP_NEW_UI_ROOT"] = str(self.root)
        extras = [self.executable.parent]
        for variable in ("PATH", "DYLD_LIBRARY_PATH" if sys.platform == "darwin" else "LD_LIBRARY_PATH"):
            if variable != "PATH" and os.name == "nt":
                continue
            env[variable] = os.pathsep.join([*(str(p) for p in extras if p.is_dir()), env.get(variable, "")])
        name = "onnxruntime.dll" if os.name == "nt" else (
            "libonnxruntime.dylib" if sys.platform == "darwin" else "libonnxruntime.so")
        runtime = self.executable.parent / name
        if self._explicit_root or runtime.is_file():
            # Keep an explicit package authoritative even when incomplete. ORT
            # consumers then report its missing library instead of borrowing an
            # inherited development runtime. Rust/wgpu-only work remains usable.
            env["ORT_DYLIB_PATH"] = str(runtime)
        return env

    def _start(self) -> None:
        if self._process and self._process.poll() is None:
            return
        self._stop()
        self._lines = queue.Queue()
        self._stderr.clear()
        self._process = subprocess.Popen([str(self.executable)], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8",
            errors="replace", bufsize=1, env=self._environment(),
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        # Capture each process's queue, so a retiring reader cannot poison a new session.
        def read_stdout(pipe, lines):
            try:
                for line in pipe:
                    lines.put(line)
            finally:
                lines.put(None)
        def read_stderr(pipe):
            for line in pipe:
                self._stderr.append(line.rstrip())
        self._readers = [threading.Thread(target=read_stdout, args=(self._process.stdout, self._lines), daemon=True),
                         threading.Thread(target=read_stderr, args=(self._process.stderr,), daemon=True)]
        for reader in self._readers:
            reader.start()
        try:
            self.ready = self._read_reply(self.timeout)
            if self.ready.get("event") != "ready":
                raise EngineError("Worker did not send a ready event", self.ready)
        except BaseException:
            self._stop()
            raise

    def _read_reply(self, timeout: float, *, report_progress: bool = True) -> dict:
        deadline = time.monotonic() + timeout
        while True:
            try:
                line = self._lines.get(timeout=max(0, deadline - time.monotonic()))
            except queue.Empty as exc:
                raise TimeoutError(f"ColorCorrect worker exceeded {timeout:g}s; session terminated") from exc
            if line is None:
                raise EngineError("Worker exited unexpectedly. " + "\n".join(self._stderr))
            try:
                reply = json.loads(line)
            except ValueError as exc:
                raise EngineError(f"Invalid worker JSON: {line[:200]}") from exc
            if not isinstance(reply, dict):
                raise EngineError("Worker returned a non-object reply")
            if reply.get("type") == "progress":
                if self.progress and report_progress:
                    self.progress(str(reply.get("message", "")))
                continue
            return reply

    def request(self, command: str, *, timeout: float | None = None,
                check: bool = True, **parameters: Any) -> dict:
        """Send any documented operation. check=True raises even for partial batches."""
        duration = self.timeout if timeout is None else timeout
        if not math.isfinite(duration) or duration <= 0:
            raise ValueError("timeout must be finite and positive")
        if "cmd" in parameters or "command" in parameters:
            raise ValueError("Pass the operation as the first argument, not cmd/command")
        if parameters.get("do_ffc") and not parameters.get("white"):
            raise ValueError("FFC requires an explicit white image (Blank.JPG in the example corpus)")
        nn = parameters.get("nn")
        if isinstance(nn, dict):
            nn = dict(nn)
            if "hidden_layers" in nn:
                layers = nn["hidden_layers"]
                layers = [layers] if type(layers) is int else layers
                if not isinstance(layers, list) or not 1 <= len(layers) <= 4 or any(type(n) is not int or not 4 <= n <= 512 for n in layers):
                    raise ValueError("nn.hidden_layers must be an integer or 1–4 integers, each from 4 to 512")
                nn["hidden_layers"] = layers
            if "optimizer" in nn and nn["optimizer"] not in ("adam", "sgd", "rmsprop"):
                raise ValueError("nn.optimizer must be adam, sgd or rmsprop")
            if "dropout" in nn and (type(nn["dropout"]) not in (int, float) or not 0 <= nn["dropout"] <= 0.8):
                raise ValueError("nn.dropout must be a number from 0 to 0.8")
            if "batch_norm" in nn and not isinstance(nn["batch_norm"], bool):
                raise ValueError("nn.batch_norm must be true or false")
            parameters["nn"] = nn
        payload = json.dumps({"cmd": command, "policy": self.policy, **parameters}, default=_json_default, allow_nan=False)
        with self._lock:
            self._start()
            if parameters.get("reference_profiles") or parameters.get("reference_assignments"):
                missing = {"custom_references_v1", "chart_identity_v1"} - set(self.ready.get("capabilities") or [])
                if missing:
                    reply = {"ok": False, "code": "unsupported_capability",
                             "error": "Worker cannot apply custom references; missing " + ", ".join(sorted(missing))}
                    if check:
                        raise EngineError(reply["error"], reply)
                    return reply
            try:
                self._process.stdin.write(payload + "\n")
                self._process.stdin.flush()
                reply = self._read_reply(duration)
            except BaseException:
                self._stop()
                raise
            if command in ("shutdown", "quit"):
                self._stop()
            failures = [r for r in reply.get("results", []) if r.get("ok") is False]
            if check and (reply.get("ok") is not True or failures):
                raise EngineError(reply.get("error") or f"{len(failures)} image(s) failed", reply)
            return reply

    def devices(self, **options):
        return self.request("devices", **options)

    def warmup(self, **options):
        return self.request("warmup", **options)

    def detect(self, image, **options):
        return self.request("detect", image=image, **options)

    def fit(self, image, *, output, report, model_out, **options):
        """Fit/calibrate and optionally correct the chart image; all artifact paths explicit."""
        return self.request("run", image=image, output=output, report=report, model_out=model_out, **options)

    def calibrate(self, image, *, folder=None, white=None, detect_options=None, **options):
        """Detect and fit in one call; return a reusable Calibration.

        Creates a unique run directory inside folder (or the system temp folder).
        Every run option remains available, including explicit artifact paths.
        A supplied handshake skips detection. White enables FFC unless explicitly
        disabled. No other stage or fitting defaults are changed.
        """
        if folder is not None:
            Path(folder).mkdir(parents=True, exist_ok=True)
        directory = Path(tempfile.mkdtemp(prefix="calibration-", dir=folder)).resolve()
        if "handshake" not in options:
            detection = self.detect(image, **(detect_options or {}))
            options["handshake"] = detection["handshake"]
        if white is not None:
            options["white"] = white
            options.setdefault("do_ffc", True)
        paths = {"output": directory / "corrected.png", "report": directory / "report.json",
                 "model_out": directory / "model.json"}
        for key in paths:
            paths[key] = options.pop(key, paths[key])
        reply = self.fit(image, **paths, **options)
        return Calibration(self, Path(paths["model_out"]).resolve(), directory, reply,
                           white, bool(options.get("do_ffc", False)),
                           {k: copy.deepcopy(options[k]) for k in ("ffc", "crop_rect") if k in options})

    def apply(self, model, images, output_dir, **options):
        if isinstance(images, (str, os.PathLike)):
            images = [images]
        return self.request("apply", model=model, images=list(images), output_dir=output_dir, **options)

    def preview(self, model, image, stage, preview_dir, **options):
        return self.request("preview_stage", model=model, image=image, stage=stage, preview_dir=preview_dir, **options)

    def export_cube(self, model, path, *, size=33, **options):
        if not isinstance(size, int) or not 2 <= size <= 129:
            raise ValueError("LUT size must be an integer from 2 to 129")
        return self.request("export_cube", model=model, path=path, size=size, **options)

    def _stop(self):
        process, self._process = self._process, None
        if process:
            if process.poll() is None:
                process.kill()
            process.wait()
            for reader in self._readers:
                reader.join(timeout=2)
            for pipe in (process.stdin, process.stdout, process.stderr):
                if pipe:
                    pipe.close()

    def close(self):
        """Flush deferred previews and stop. In-flight requests finish first."""
        with self._lock:
            try:
                if self._process and self._process.poll() is None:
                    self._process.stdin.write('{"cmd":"shutdown"}\n')
                    self._process.stdin.flush()
                    self._read_reply(min(self.timeout, 10), report_progress=False)
            except (EngineError, OSError, ValueError, TimeoutError):
                # Cleanup must not mask the error that caused context exit.
                pass
            finally:
                self._stop()

    def __enter__(self):
        with self._lock:
            self._start()
        return self

    def __exit__(self, *exc):
        self.close()


class Calibration:
    """A saved model plus its fit reply. Use while its Engine context is open.

    Model files remain usable after close via Engine.apply(model, ...).
    """
    def __init__(self, engine, model, folder, report, white=None, do_ffc=False, apply_defaults=None):
        self.engine, self.model, self.folder, self.report = engine, model, folder, report
        self.white, self.do_ffc = white, do_ffc
        self.apply_defaults = apply_defaults or {}

    def apply(self, images, output_dir=None, **options):
        options = {**copy.deepcopy(self.apply_defaults), **options}
        options.setdefault("do_ffc", self.do_ffc)
        if self.white is not None:
            options.setdefault("white", self.white)
        if output_dir is None:
            output_dir = tempfile.mkdtemp(prefix="applied-", dir=self.folder)
        return self.engine.apply(self.model, images, output_dir, **options)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=COMMANDS)
    parser.add_argument("--json", default="{}", help="JSON parameters, @file.json, or - for stdin")
    parser.add_argument("--engine", help="Path to ccp-engine executable")
    parser.add_argument("--root", help="App resource folder with models/ and refs/")
    parser.add_argument("--policy", default="auto", help="auto (default), directml, webgpu, vulkan; cpu selects CPU LUTs with GPU inference")
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


# Additive high-level API. Keeping this module and all original names preserves
# imports, python -m colorcorrect, and direct api/colorcorrect.py invocations.
from colorcorrect_sdk import (
    Client, CalibrationModel, ReferenceProfile, DetectionOptions, CalibrationOptions,
    ApplyOptions, FFCOptions, NeuralNetworkOptions, DetectionResult, DetectedChart,
    BackendInfo, ImageResult, ApplyResult, LUTResult, ColorCorrectError,
    InvalidInputError, MissingRuntimeError, UnsupportedCapabilityError,
    ProcessingError, ProcessingTimeoutError, ClosedClientError, BatchProcessingError,
)
from colorcorrect_pipeline import ColorCorrection, Config


if __name__ == "__main__":
    raise SystemExit(main())
