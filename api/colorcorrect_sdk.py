"""Typed, dependency-free workflows for the native ColorCorrect worker.

Import public names from :mod:`colorcorrect`. The low-level Engine transport
remains available for advanced protocol operations and existing applications.
"""
from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import asdict, dataclass, field, replace
import copy
import csv
import hashlib
import io
import json
import math
import os
from pathlib import Path
import tempfile
import time
from typing import Any, Callable
from uuid import uuid4

PathLike = str | os.PathLike[str]


def _content_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()

RGB_SPACES = ("srgb", "linear-srgb", "display-p3", "adobe-rgb-1998", "rec2020",
              "linear-rec2020", "prophoto-rgb", "acescg")
WHITE_POINTS = ("d50", "d55", "d60", "d65", "d75", "a", "c", "e")
CHARTS = {"classic": (4, 6), "passport": (4, 6), "spyder": (4, 6), "digital-sg": (10, 14)}


class ColorCorrectError(RuntimeError):
    """Base error for high-level workflows; raw_reply preserves diagnostics."""
    def __init__(self, message: str, raw_reply: dict | None = None):
        super().__init__(message)
        self.raw_reply = raw_reply


class InvalidInputError(ColorCorrectError, ValueError):
    """A path, reference, option, or selection is invalid."""


class MissingRuntimeError(ColorCorrectError, FileNotFoundError):
    """Install/extract native resources, or specify executable= and root=."""


class UnsupportedCapabilityError(ColorCorrectError):
    """The worker is too old or lacks a requested feature."""


class ProcessingError(ColorCorrectError):
    """Native processing or the worker protocol failed."""


class ProcessingTimeoutError(ColorCorrectError, TimeoutError):
    """The worker exceeded its command/startup timeout and was terminated."""


class ClosedClientError(ColorCorrectError):
    """Create a new Client and load the saved model to continue."""


def _choice(value: str, choices: Sequence[str], name: str) -> str:
    if value not in choices:
        raise InvalidInputError(f"{name} must be one of {', '.join(choices)}; got {value!r}")
    return value


def _finite(value: float, name: str, lower: float | None = None, upper: float | None = None) -> None:
    if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value):
        raise InvalidInputError(f"{name} must be finite")
    if lower is not None and value < lower or upper is not None and value > upper:
        raise InvalidInputError(f"{name} must be between {lower} and {upper}")


def _integer(value: int, name: str, lower: int, upper: int) -> None:
    if type(value) is not int or not lower <= value <= upper:
        raise InvalidInputError(f"{name} must be an integer from {lower} to {upper}")


def _input(path: PathLike, name: str = "input") -> Path:
    try:
        value = Path(path).expanduser().resolve()
    except (TypeError, ValueError, OSError) as exc:
        raise InvalidInputError(f"Invalid {name} path: {path!r}") from exc
    if not value.is_file():
        raise InvalidInputError(f"{name} file does not exist: {value}")
    return value


def _json(path: Path) -> dict:
    def unique_object(pairs):
        result = {}
        for key, item in pairs:
            if key in result:
                raise ValueError(f"duplicate JSON field: {key}")
            result[key] = item
        return result
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)
        if not isinstance(value, dict):
            raise ValueError("expected an object")
        return value
    except (OSError, ValueError) as exc:
        raise InvalidInputError(f"Cannot read JSON object from {path}: {exc}") from exc


def _artifact(path: PathLike, name: str, reply: dict) -> Path:
    try:
        return _input(path, name)
    except InvalidInputError as exc:
        raise ProcessingError(f"Worker did not produce a readable {name}: {path}", reply) from exc


def _replace_json_atomic(temporary: str, path: Path) -> None:
    # Windows denies rename-over-open when another handle lacks delete sharing.
    # Brief readers must not turn an otherwise completed fit into a failed save.
    # Retain atomic replacement and propagate permanent denial after <=310 ms.
    for attempt in range(6):
        try:
            os.replace(temporary, path)
            return
        except OSError as exc:
            if os.name != "nt" or getattr(exc, "winerror", None) not in (5, 32, 33) or attempt == 5:
                raise
            time.sleep(0.01 * (2 ** attempt))


def _write_json(path: Path, value: dict, *, overwrite: bool = False) -> Path:
    path = Path(path).expanduser().resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    if overwrite:
        handle, temporary = tempfile.mkstemp(prefix=".colorcorrect-", suffix=".json", dir=path.parent)
        try:
            with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
                json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
                stream.write("\n")
            _replace_json_atomic(temporary, path)
        except BaseException:
            try:
                Path(temporary).unlink(missing_ok=True)
            except OSError:
                # A cleanup denial must not hide the original write/replace error.
                pass
            raise
        return path
    try:
        with path.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
            stream.write("\n")
    except FileExistsError as exc:
        raise InvalidInputError(f"Destination exists: {path}; use overwrite=True to replace it") from exc
    return path


@dataclass(frozen=True)
class ReferenceProfile:
    """Complete chart reference snapshot. Lab uses ordinary L*a*b* and scale=1.

    XYZ scales are 1 or 100; RGB scales are 1, 255, or 65535. Values are kept
    unclipped; the worker performs the authoritative conversion to Lab D50.
    """
    values: tuple[tuple[float, float, float], ...]
    chart: str = "classic"
    color_space: str = "lab"
    white_point: str = "d50"
    scale: float = 1
    order: str = "row-major"
    name: str = "Custom reference"
    id: str = field(default_factory=lambda: str(uuid4()))
    revision: int = 1
    version: int = 1

    def __post_init__(self):
        _choice(self.chart, tuple(CHARTS), "chart")
        _choice(self.color_space, ("lab", "xyz", *RGB_SPACES), "color_space")
        _choice(self.white_point, WHITE_POINTS, "white_point")
        _choice(self.order, ("row-major", "column-major"), "order")
        if self.version != 1 or type(self.version) is not int:
            raise UnsupportedCapabilityError(f"Unsupported reference schema version: {self.version}")
        _integer(self.revision, "revision", 1, 2**31 - 1)
        if not isinstance(self.id, str) or not self.id.strip() or not isinstance(self.name, str) or not self.name.strip():
            raise InvalidInputError("Reference id and name must be nonempty strings")
        _finite(self.scale, "scale")
        allowed = (1,) if self.color_space == "lab" else (1, 100) if self.color_space == "xyz" else (1, 255, 65535)
        if self.scale not in allowed:
            raise InvalidInputError(f"{self.color_space} scale must be one of {allowed}")
        try:
            values = tuple(tuple(row) for row in self.values)
        except TypeError as exc:
            raise InvalidInputError("values must contain numeric triplets") from exc
        rows, cols = CHARTS[self.chart]
        if len(values) != rows * cols:
            raise InvalidInputError(f"{self.chart} requires {rows * cols} triplets ({rows} rows × {cols} columns)")
        for index, row in enumerate(values):
            if len(row) != 3:
                raise InvalidInputError(f"Cell {index + 1} must contain exactly three values")
            for value in row:
                _finite(value, f"Cell {index + 1}")
            if self.color_space == "lab" and not 0 <= row[0] <= 100:
                raise InvalidInputError(f"Cell {index + 1}: Lab L* must be between 0 and 100")
            if self.color_space == "xyz" and any(value < 0 for value in row):
                raise InvalidInputError(f"Cell {index + 1}: XYZ values must be nonnegative")
            if self.color_space in RGB_SPACES and any(not 0 <= value <= self.scale for value in row):
                raise InvalidInputError(f"Cell {index + 1}: RGB values must be between 0 and {self.scale}")
        object.__setattr__(self, "values", tuple(tuple(float(v) for v in row) for row in values))

    @classmethod
    def from_values(cls, values: Sequence[Sequence[float]], *, chart: str = "classic",
                    color_space: str = "lab", white_point: str = "d50", scale: float = 1,
                    order: str = "row-major", name: str = "Custom reference",
                    id: str | None = None, revision: int = 1) -> ReferenceProfile:
        return cls(values=values, chart=chart, color_space=color_space, white_point=white_point,
                   scale=scale, order=order, name=name, id=id if id is not None else str(uuid4()), revision=revision)

    @classmethod
    def from_csv(cls, path: PathLike, *, chart: str = "classic", color_space: str = "lab",
                 white_point: str = "d50", scale: float = 1, order: str = "row-major",
                 name: str | None = None) -> ReferenceProfile:
        """Read triplets, ``cell,x,y,z`` or 1-based ``row,column,x,y,z``.

        A single optional header is allowed. Comma, tab, and semicolon delimiters
        are accepted. Coordinate rows must enumerate each chart cell exactly once.
        """
        source = _input(path, "reference CSV")
        try:
            text = source.read_text(encoding="utf-8-sig")
        except (OSError, UnicodeError) as exc:
            raise InvalidInputError(f"Cannot read reference CSV as UTF-8: {source}: {exc}") from exc
        return cls.from_text(text, chart=chart,
                             color_space=color_space, white_point=white_point, scale=scale,
                             order=order, name=name or source.stem)

    @classmethod
    def from_text(cls, text: str, *, chart: str = "classic", color_space: str = "lab",
                  white_point: str = "d50", scale: float = 1, order: str = "row-major",
                  name: str = "Custom reference") -> ReferenceProfile:
        """Parse CSV or spreadsheet-pasted tabular text with the same CSV contract."""
        if not isinstance(text, str):
            raise InvalidInputError("Reference text must be a string")
        _choice(chart, tuple(CHARTS), "chart")
        _choice(order, ("row-major", "column-major"), "order")
        try:
            dialect = csv.Sniffer().sniff(text[:8192], delimiters=",;\t")
        except csv.Error:
            dialect = csv.excel
        try:
            rows = [row for row in csv.reader(io.StringIO(text), dialect) if any(cell.strip() for cell in row)]
        except csv.Error as exc:
            raise InvalidInputError(f"Invalid reference table: {exc}") from exc
        if not rows:
            raise InvalidInputError("Reference table is empty")
        headers = {"l", "l*", "x", "r", "red", "row", "cell", "coordinate", "patch", "v1"}
        if rows[0][0].strip().lower() in headers:
            header = [cell.strip().lower().replace("*", "") for cell in rows[0]]
            if header[0] == "row":
                if len(header) != 5 or header[1] not in ("column", "col"):
                    raise InvalidInputError("Coordinate header must be row,column followed by three channel names")
                header = header[2:]
            elif header[0] in ("cell", "coordinate", "patch"):
                if len(header) != 4:
                    raise InvalidInputError("Coordinate header must be cell followed by three channel names")
                header = header[1:]
            if header == ["red", "green", "blue"]:
                header = ["r", "g", "b"]
            expected = ["l", "a", "b"] if color_space == "lab" else ["x", "y", "z"] if color_space == "xyz" else ["r", "g", "b"]
            if header != expected and header != ["v1", "v2", "v3"]:
                raise InvalidInputError(f"Reference header {header} does not match declared {color_space} channels {expected}")
            rows = rows[1:]
        if not rows or len(rows[0]) not in (3, 4, 5) or any(len(row) != len(rows[0]) for row in rows):
            raise InvalidInputError("Expected three values per row, or cell/row,column plus three values")
        values: Any = []
        try:
            if len(rows[0]) in (4, 5):
                height, width = CHARTS[chart]
                indexed = {}
                for row in rows:
                    if len(row) == 5:
                        r, c = int(row[0]), int(row[1])
                    else:
                        coordinate = row[0].strip().upper()
                        if len(coordinate) < 2 or not "A" <= coordinate[0] <= "Z":
                            raise InvalidInputError(f"Invalid cell coordinate {coordinate!r}")
                        r, c = int(coordinate[1:]), ord(coordinate[0]) - ord("A") + 1
                    if not 1 <= r <= height or not 1 <= c <= width or (r, c) in indexed:
                        raise InvalidInputError(f"Duplicate or out-of-range cell ({r}, {c})")
                    indexed[r, c] = tuple(float(v) for v in row[-3:])
                if len(indexed) != height * width:
                    raise InvalidInputError("Reference table is missing chart cells")
                values = [indexed[r, c] for r in range(1, height + 1) for c in range(1, width + 1)]
                order = "row-major"
            else:
                values = [tuple(float(v) for v in row) for row in rows]
        except (ValueError, TypeError) as exc:
            raise InvalidInputError(f"Invalid numeric reference value: {exc}") from exc
        return cls.from_values(values, chart=chart, color_space=color_space, white_point=white_point,
                               scale=scale, order=order, name=name)

    @classmethod
    def from_json(cls, path: PathLike) -> ReferenceProfile:
        value = _json(_input(path, "reference JSON"))
        required = ("version", "id", "name", "revision", "chart", "color_space", "white_point", "scale", "order", "values")
        missing = [key for key in required if key not in value]
        if missing:
            raise InvalidInputError(f"Reference JSON is missing: {', '.join(missing)}")
        unknown = sorted(set(value) - set(required))
        if unknown:
            raise InvalidInputError(f"Unsupported reference JSON fields: {', '.join(unknown)}")
        return cls(**{key: value[key] for key in required})

    def to_dict(self) -> dict:
        value = asdict(self)
        value["values"] = [list(row) for row in self.values]
        return value

    def to_json(self, path: PathLike, *, overwrite: bool = False) -> Path:
        return _write_json(Path(path), self.to_dict(), overwrite=overwrite)


@dataclass(frozen=True)
class DetectionOptions:
    confidence: float | None = None
    crop: float = 0.75
    prefer: str | None = None
    allow_recall: bool = True

    def to_dict(self) -> dict:
        _finite(self.crop, "crop", 0.01, 1)
        if self.confidence is not None:
            _finite(self.confidence, "confidence", 0, 1)
        if self.prefer is not None:
            _choice(self.prefer, tuple(CHARTS), "prefer")
        if type(self.allow_recall) is not bool:
            raise InvalidInputError("allow_recall must be bool")
        return {"crop": self.crop, "allow_recall": self.allow_recall,
                **({"conf": self.confidence} if self.confidence is not None else {}),
                **({"prefer": self.prefer} if self.prefer is not None else {})}


@dataclass(frozen=True)
class FFCOptions:
    method: str = "poly"
    bins: int = 50
    smooth: int = 5
    degree: int = 5
    nn_max_iter: int = 10000
    tol: float = 1e-8
    cut_off: float = 1.3
    max_val: float = 1.8

    def to_dict(self) -> dict:
        _choice(self.method, ("linear", "poly", "pls", "nn"), "ffc.method")
        _integer(self.bins, "ffc.bins", 2, 100000)
        _integer(self.degree, "ffc.degree", 1, 32)
        _integer(self.smooth, "ffc.smooth", 1, 100001)
        _integer(self.nn_max_iter, "ffc.nn_max_iter", 1, 10000000)
        if self.smooth % 2 != 1:
            raise InvalidInputError("ffc.smooth must be odd")
        for key in ("tol", "cut_off", "max_val"):
            _finite(getattr(self, key), "ffc." + key, 1e-15)
        if self.max_val <= self.cut_off:
            raise InvalidInputError("ffc.max_val must be greater than ffc.cut_off")
        return asdict(self)


@dataclass(frozen=True)
class NeuralNetworkOptions:
    hidden_layers: tuple[int, ...] = (100,)
    optimizer: str = "adam"
    batch_norm: bool = False
    dropout: float = 0
    epochs: int = 10000
    patience: int = 1500
    lr: float = 0.001
    tol: float = 1e-6

    def to_dict(self) -> dict:
        if not isinstance(self.hidden_layers, (tuple, list)) or not 1 <= len(self.hidden_layers) <= 4:
            raise InvalidInputError("nn.hidden_layers requires 1–4 widths")
        for width in self.hidden_layers:
            _integer(width, "nn.hidden_layers width", 4, 512)
        _choice(self.optimizer, ("adam", "sgd", "rmsprop"), "nn.optimizer")
        _finite(self.dropout, "nn.dropout", 0, 0.8)
        for key in ("epochs", "patience"):
            _integer(getattr(self, key), "nn." + key, 1, 10000000)
        for key in ("lr", "tol"):
            _finite(getattr(self, key), "nn." + key, 1e-15)
        if type(self.batch_norm) is not bool:
            raise InvalidInputError("nn.batch_norm must be bool")
        return {**asdict(self), "hidden_layers": list(self.hidden_layers)}


@dataclass(frozen=True)
class CalibrationOptions:
    model: str = "rpcc2"
    do_ffc: bool | None = None
    do_gc: bool = True
    do_wb: bool = True
    do_cc: bool = True
    do_sat: bool = True
    skip_saturated: bool = False
    crop: float = 0.75
    fit_samples: int = 9
    degree: int = 2
    include_combinations: bool = True
    pls_components: int = 0
    nn: NeuralNetworkOptions = field(default_factory=NeuralNetworkOptions)
    ffc: FFCOptions = field(default_factory=FFCOptions)
    crop_rect: str | None = None
    input_space: str = "srgb"
    input_white: str = "d65"
    target_space: str = "srgb"
    target_white: str = "d65"
    clip_lo: float = -0.2
    clip_hi: float = 1.3
    cross_validate: bool = True
    phase2_teacher: bool = False
    write_output: bool = True
    fast_mode: bool = True

    def to_dict(self) -> dict:
        _choice(self.model, ("ccm", "rpcc2", "rpcc3", "linear", "pls", "nn"), "model")
        for key in ("input_space", "target_space"):
            _choice(getattr(self, key), RGB_SPACES, key)
        for key in ("input_white", "target_white"):
            _choice(getattr(self, key), WHITE_POINTS, key)
        _finite(self.crop, "crop", 0.01, 1)
        _integer(self.fit_samples, "fit_samples", 1, 100000)
        if self.do_cc and self.model == "linear":
            _integer(self.degree, "degree", 1, 12)
        if self.do_cc and self.model == "pls":
            _integer(self.pls_components, "pls_components", 0, 10000)
        _finite(self.clip_lo, "clip_lo")
        _finite(self.clip_hi, "clip_hi")
        if self.clip_lo >= self.clip_hi:
            raise InvalidInputError("clip_lo must be less than clip_hi")
        if self.do_ffc is not False:
            _validate_crop(self.crop_rect)
        values = asdict(self)
        for key, value in values.items():
            if key.startswith("do_") or key in ("skip_saturated", "include_combinations", "cross_validate", "phase2_teacher", "write_output", "fast_mode"):
                if not (key == "do_ffc" and value is None) and type(value) is not bool:
                    raise InvalidInputError(f"{key} must be bool")
        if not self.fast_mode:
            values.pop("fast_mode")
        for key in ("input_space", "input_white", "target_space", "target_white", "pls_components"):
            values.pop(key)
        values.pop("nn")
        values.pop("ffc")
        if self.do_cc and self.model == "nn":
            if not isinstance(self.nn, NeuralNetworkOptions):
                raise InvalidInputError("Use NeuralNetworkOptions for nn")
            values["nn"] = self.nn.to_dict()
        if self.do_ffc is not False:
            if not isinstance(self.ffc, FFCOptions):
                raise InvalidInputError("Use FFCOptions for ffc")
            values["ffc"] = self.ffc.to_dict()
        if self.do_cc and self.model == "pls":
            values["pls"] = {"components": self.pls_components}
        values.update(spaces={"input": self.input_space, "target": self.target_space},
                      whites={"input": self.input_white, "target": self.target_white})
        return {key: value for key, value in values.items() if value is not None}


@dataclass(frozen=True)
class ApplyOptions:
    output_format: str = "png"
    do_ffc: bool | None = None
    do_gc: bool | None = None
    do_wb: bool | None = None
    do_cc: bool | None = None
    white: PathLike | None = None
    ffc: FFCOptions | None = None
    crop_rect: str | None = None
    write_output: bool = True
    fast_mode: bool = True

    def to_dict(self) -> dict:
        _choice(self.output_format, ("png", "jpg"), "output_format")
        if type(self.fast_mode) is not bool:
            raise InvalidInputError("fast_mode must be bool")
        for key in ("do_ffc", "do_gc", "do_wb", "do_cc", "write_output"):
            value = getattr(self, key)
            if value is not None and type(value) is not bool:
                raise InvalidInputError(f"{key} must be bool")
        if self.do_ffc is not False:
            _validate_crop(self.crop_rect)
        values = {key: getattr(self, key) for key in ("output_format", "do_ffc", "do_gc", "do_wb", "do_cc", "write_output", "crop_rect")}
        # Older workers use the standard pipeline when this option is omitted.
        if self.fast_mode:
            values["fast_mode"] = True
        if self.white is not None and self.do_ffc is not False:
            values["white"] = _input(self.white, "FFC white")
        if self.ffc is not None and self.do_ffc is not False:
            if not isinstance(self.ffc, FFCOptions):
                raise InvalidInputError("ffc must be FFCOptions")
            values["ffc"] = self.ffc.to_dict()
        return {key: value for key, value in values.items() if value is not None}


def _validate_crop(crop: str | None) -> None:
    if crop is not None:
        try:
            x1, y1, x2, y2 = (int(v) for v in crop.split(","))
            if min(x1, y1) < 0 or x2 <= x1 or y2 <= y1:
                raise ValueError()
        except (AttributeError, TypeError, ValueError) as exc:
            raise InvalidInputError("crop_rect must be x1,y1,x2,y2 with positive extent") from exc


@dataclass(frozen=True)
class BackendInfo:
    requested: str
    actual: dict[str, str]
    diagnostics: dict[str, Any]


def _backends(reply: dict, policy: str) -> BackendInfo:
    # Only populate actual when the worker reports it, never infer GPU execution
    # from the requested policy or a registered execution provider.
    actual = {key.removesuffix("_backend"): str(reply[key])
              for key in ("apply_backend", "plane_backend", "detect_backend") if reply.get(key)}
    if reply.get("device"):
        actual["detect"] = str(reply["device"])
    return BackendInfo(policy, actual, {key: copy.deepcopy(reply[key]) for key in
                       ("device", "runtime", "assignment", "accelerators", "fallback", "inference", "provider", "backend", "cached", "execution", "completed_stages", "frames_processed") if key in reply})


def _warnings(reply: dict) -> tuple[str, ...]:
    warnings = reply.get("warnings", [])
    result = [warnings] if isinstance(warnings, str) else [str(value) for value in warnings or []]
    if reply.get("ffc_skipped"):
        result.append("FFC skipped: " + str(reply["ffc_skipped"]))
    execution = reply.get("execution")
    if isinstance(execution, dict):
        result.extend(f"Requested {stage.upper()} stage did not execute; the saved model has no corresponding transform"
                      for stage in execution.get("skipped_stages", []) if isinstance(stage, str))
    return tuple(result)


@dataclass(frozen=True)
class DetectedChart:
    id: str
    chart: str
    rows: int
    columns: int
    score: float | None
    geometry: dict[str, Any]


@dataclass(frozen=True)
class DetectionResult:
    image: Path
    snapshot_id: str
    charts: tuple[DetectedChart, ...]
    backends: BackendInfo
    warnings: tuple[str, ...]
    raw_reply: dict[str, Any]
    _handshake_json: str = field(repr=False)
    _image_signature: tuple[int, int] = field(repr=False)

    @property
    def handshake(self) -> dict:
        """Return a copy; editing it cannot change this detection snapshot."""
        return json.loads(self._handshake_json)


@dataclass(frozen=True)
class ImageResult:
    image: Path
    ok: bool
    output: Path | None
    error: str | None
    warnings: tuple[str, ...]
    backends: BackendInfo


@dataclass(frozen=True)
class ApplyResult:
    folder: Path
    images: tuple[ImageResult, ...]
    backends: BackendInfo
    warnings: tuple[str, ...]
    raw_reply: dict[str, Any]

    @property
    def outputs(self) -> tuple[Path, ...]:
        return tuple(row.output for row in self.images if row.ok and row.output is not None)

    @property
    def failures(self) -> tuple[ImageResult, ...]:
        return tuple(row for row in self.images if not row.ok)


class BatchProcessingError(ProcessingError):
    """Partial/total batch failure; result includes every success and failure."""
    def __init__(self, result: ApplyResult):
        self.result = result
        super().__init__(f"{len(result.failures)} of {len(result.images)} images failed; inspect .result", result.raw_reply)


@dataclass(frozen=True)
class LUTResult:
    path: Path
    size: int
    warnings: tuple[str, ...]
    raw_reply: dict[str, Any]


def _chart_name(face: dict) -> str:
    value = str(face.get("cli_chart") or face.get("chart_type") or "classic").lower().replace("_", "-")
    aliases = {"classic-4x6": "classic", "spyder-4x6": "spyder", "digital-sg-10x14": "digital-sg"}
    return aliases.get(value, value)


def _run_directory(output_dir: PathLike, prefix: str) -> Path:
    try:
        folder = Path(output_dir).expanduser().resolve()
        folder.mkdir(parents=True, exist_ok=True)
        return Path(tempfile.mkdtemp(prefix=prefix + "-", dir=folder))
    except (TypeError, ValueError, OSError) as exc:
        raise InvalidInputError(f"Cannot create output directory {output_dir!r}: {exc}") from exc


def _output_root(output_dir: PathLike) -> Path:
    try:
        folder = Path(output_dir).expanduser().resolve()
    except (TypeError, ValueError, OSError) as exc:
        raise InvalidInputError(f"Invalid output_dir: {output_dir!r}") from exc
    for parent in (folder, *folder.parents):
        if parent.exists():
            if not parent.is_dir():
                raise InvalidInputError(f"Output directory has a file in its path: {parent}")
            break
    return folder


class Client:
    """High-level client. Native app resources are required; selection is automatic.

    Closing is permanent. Keep this client open while using CalibrationModel
    handles; later open a new Client and call load_calibration(model.path).
    """
    def __init__(self, executable: PathLike | None = None, *, root: PathLike | None = None,
                 backend: str = "auto", timeout: float = 600,
                 progress: Callable[[str], None] | None = None):
        from colorcorrect import Engine
        _choice(backend, ("cpu", "cpu-inference", "auto", "directml", "webgpu", "vulkan"), "backend")
        _finite(timeout, "timeout", 1e-12)
        if root is not None:
            try:
                supplied_root = Path(root).expanduser()
            except (TypeError, ValueError, OSError) as exc:
                raise InvalidInputError(f"Invalid native application root: {root!r}") from exc
            if not (supplied_root / "refs/neutrals.json").is_file():
                # Keep managed assemblies in Resources so macOS can seal the
                # bundle without treating them as unsigned native executables.
                for bundle_root in (supplied_root / "Contents/Resources/runtime",
                                    supplied_root / "Contents/MacOS"):
                    if (bundle_root / "refs/neutrals.json").is_file():
                        root = bundle_root
                        break
        try:
            self._engine = Engine(executable, root=root, policy=backend, timeout=timeout, progress=progress)
        except FileNotFoundError as exc:
            raise MissingRuntimeError(str(exc)) from exc
        self._closed = False
        self.backend = backend

    def _check_open(self) -> None:
        if self._closed:
            raise ClosedClientError("Client is closed. Open a new Client and load_calibration(model.path).")

    def _start(self) -> None:
        self._check_open()
        try:
            with self._engine._lock:
                self._engine._start()
        except TimeoutError as exc:
            raise ProcessingTimeoutError(str(exc)) from exc
        except OSError as exc:
            raise MissingRuntimeError(f"Cannot launch native worker: {exc}") from exc
        except RuntimeError as exc:
            raise ProcessingError(str(exc), getattr(exc, "reply", None)) from exc

    @property
    def capabilities(self) -> frozenset[str]:
        self._start()
        return frozenset(self._engine.ready.get("capabilities", []))

    @property
    def engine(self):
        """Advanced Engine escape hatch. Its original request API is unchanged."""
        self._check_open()
        return self._engine

    def _request(self, command: str, *, check: bool = True, **parameters) -> dict:
        self._check_open()
        from colorcorrect import EngineError
        try:
            return self._engine.request(command, check=check, **parameters)
        except TimeoutError as exc:
            raise ProcessingTimeoutError(str(exc)) from exc
        except EngineError as exc:
            message = str(exc)
            if "built without" in message or (exc.reply or {}).get("code") == "unsupported_capability":
                raise UnsupportedCapabilityError(message, exc.reply) from exc
            raise ProcessingError(message, exc.reply) from exc
        except OSError as exc:
            raise MissingRuntimeError(f"Native runtime unavailable: {exc}") from exc

    def detect(self, image: PathLike, *, options: DetectionOptions | None = None) -> DetectionResult:
        self._check_open()
        path = _input(image, "chart image")
        if options is not None and not isinstance(options, DetectionOptions):
            raise InvalidInputError("options must be DetectionOptions")
        parameters = (options or DetectionOptions()).to_dict()
        reply = self._request("detect", image=path, **parameters)
        if reply.get("n_charts") == 0:
            raise ProcessingError("No chart was detected", reply)
        source = reply.get("handshake")
        if isinstance(source, dict):
            handshake = copy.deepcopy(source)
        elif isinstance(source, str):
            handshake = _json(_artifact(source, "detection handshake", reply))
        else:
            raise ProcessingError("Detection returned no handshake", reply)
        snapshot_id = str(handshake.get("snapshot_id") or uuid4())
        handshake["snapshot_id"] = snapshot_id
        faces = handshake.get("charts") or handshake.get("faces") or [handshake.copy()]
        if not isinstance(faces, list) or not faces:
            raise ProcessingError("Detection returned no charts", reply)
        charts = []
        seen = set()
        for face in faces:
            if not isinstance(face, dict):
                raise ProcessingError("Detection returned invalid chart geometry", reply)
            chart_id = str(face.get("chart_id") or f"{snapshot_id}:{len(charts)}")
            if chart_id in seen:
                raise ProcessingError("Detection returned duplicate chart identities", reply)
            seen.add(chart_id)
            face["chart_id"] = chart_id
            kind = _chart_name(face)
            height, width = CHARTS.get(kind, (0, 0))
            charts.append(DetectedChart(chart_id, kind, int(face.get("rows", height)),
                          int(face.get("cols", width)), face.get("score"),
                          {key: copy.deepcopy(face[key]) for key in ("quad", "corners", "patch_cells", "patch_obbs") if key in face}))
        handshake["charts"] = faces
        if "faces" in handshake:
            handshake["faces"] = copy.deepcopy(faces)
        handshake["chart_id"] = faces[0]["chart_id"]
        merged = {**handshake, **reply}
        signature = path.stat()
        return DetectionResult(path, snapshot_id, tuple(charts), _backends(merged, self.backend),
                               _warnings(reply), copy.deepcopy(reply), json.dumps(handshake, allow_nan=False),
                               (signature.st_size, signature.st_mtime_ns))

    def calibrate(self, image: PathLike, *, output_dir: PathLike,
                  reference: ReferenceProfile | None = None,
                  references: Mapping[str, ReferenceProfile] | None = None,
                  detection: DetectionResult | None = None,
                  chart_ids: Sequence[str] | None = None, white: PathLike | None = None,
                  options: CalibrationOptions | None = None,
                  detection_options: DetectionOptions | None = None) -> CalibrationModel:
        self._check_open()
        image_path = _input(image, "chart image")
        output_root = _output_root(output_dir)
        if options is not None and not isinstance(options, CalibrationOptions):
            raise InvalidInputError("options must be CalibrationOptions")
        opts = options or CalibrationOptions()
        do_ffc = opts.do_ffc if opts.do_ffc is not None else white is not None
        white_path = _input(white, "FFC white") if do_ffc and white is not None else None
        parameters = replace(opts, do_ffc=do_ffc).to_dict()
        if do_ffc and white_path is None:
            raise InvalidInputError("FFC requires white= with an existing flat-field image")
        if (opts.input_space != "srgb" or opts.input_white != "d65") and (do_ffc or opts.do_gc or opts.do_wb):
            raise InvalidInputError("Non-sRGB/D65 input requires do_ffc=False, do_gc=False and do_wb=False")
        if reference is not None and references is not None:
            raise InvalidInputError("Use reference= for one chart or references= for explicit chart assignments")
        if reference is not None and not isinstance(reference, ReferenceProfile):
            raise InvalidInputError("reference must be a ReferenceProfile")
        if references is not None and (not isinstance(references, Mapping) or not references or
                                      any(not isinstance(value, ReferenceProfile) for value in references.values())):
            raise InvalidInputError("references must be a nonempty chart-id → ReferenceProfile mapping")
        if reference is not None or references is not None:
            missing = {"custom_references_v1", "chart_identity_v1"} - self.capabilities
            if missing:
                raise UnsupportedCapabilityError("Worker cannot apply custom references; missing " + ", ".join(sorted(missing)))
        if detection is not None and (not isinstance(detection, DetectionResult) or detection.image != image_path):
            raise InvalidInputError("Detection must belong to this chart image")
        if detection is not None:
            info = image_path.stat()
            if (info.st_size, info.st_mtime_ns) != detection._image_signature:
                raise InvalidInputError("Chart image changed since detection; detect again before assigning references")
        if chart_ids is not None and (isinstance(chart_ids, str) or any(not isinstance(key, str) for key in chart_ids)):
            raise InvalidInputError("chart_ids must be a sequence of chart identity strings")
        if opts.fast_mode and "combined_lut65_v1" not in self.capabilities:
            raise UnsupportedCapabilityError("This worker cannot use fast mode; update the native engine")
        detected = detection or self.detect(image_path, options=detection_options)
        if reference is not None and chart_ids is None and len(detected.charts) != 1:
            raise InvalidInputError("Multiple charts detected: select one explicitly with chart_ids=[chart.id], or use references={chart.id: profile}")
        selected_ids = list(chart_ids) if chart_ids is not None else list(references) if references else [detected.charts[0].id]
        chart_map = {chart.id: chart for chart in detected.charts}
        if not selected_ids or len(set(selected_ids)) != len(selected_ids) or any(key not in chart_map for key in selected_ids):
            raise InvalidInputError("chart_ids must contain unique IDs from this DetectionResult")
        assignments = dict(references or {})
        if reference is not None:
            if len(selected_ids) != 1:
                raise InvalidInputError("A single reference requires exactly one selected chart; use references={chart_id: profile}")
            assignments[selected_ids[0]] = reference
        if assignments and set(assignments) != set(selected_ids):
            raise InvalidInputError("Custom mode requires an explicit reference for every selected chart and no unselected chart IDs")
        profiles = {}
        for chart_id, profile in assignments.items():
            kind = chart_map[chart_id].chart
            if not (profile.chart == kind or {profile.chart, kind} <= {"classic", "passport"}):
                raise InvalidInputError(f"Profile {profile.name!r} ({profile.chart}) is incompatible with chart {chart_id} ({kind})")
            key = (profile.id, profile.revision)
            value = profile.to_dict()
            if key in profiles and profiles[key] != value:
                raise InvalidInputError("Two different profiles have the same id and revision")
            profiles[key] = value
        handshake = detected.handshake
        all_faces = handshake["charts"]
        faces_by_id = {face["chart_id"]: face for face in all_faces}
        # Select by immutable identity, never by a newly reordered index.
        faces = [faces_by_id[key] for key in selected_ids]
        handshake.update(copy.deepcopy(faces[0]))
        handshake.update(snapshot_id=detected.snapshot_id, charts=faces, selected=0,
                         n_charts=len(faces), use_all=len(faces) > 1)
        if "faces" in handshake:
            handshake["faces"] = copy.deepcopy(faces)
        parameters.update(handshake=handshake, face=0, use_all=len(faces) > 1, do_ffc=do_ffc)
        if assignments:
            parameters.update(reference_profiles=list(profiles.values()), reference_assignments=[
                {"chart_id": key, "profile_id": profile.id, "profile_revision": profile.revision}
                for key, profile in assignments.items()])
        if white_path is not None:
            parameters["white"] = white_path
        white_hash = _content_hash(white_path) if do_ffc and white_path else None
        folder = _run_directory(output_root, "calibration")
        paths = {"output": folder / "corrected.png", "report": folder / "report.json", "model_out": folder / "model.json"}
        reply = self._request("run", image=image_path, **paths, **parameters)
        if white_hash and _content_hash(white_path) != white_hash:
            raise ProcessingError("FFC white image changed during calibration; repeat with a stable image", reply)
        model_path = _artifact(reply.get("model_out") or paths["model_out"], "saved model", reply)
        saved = _json(model_path)
        report = reply.get("report_json") or {}
        warnings = list(_warnings(reply)) + list(_warnings(report))
        if do_ffc and not report.get("do_ffc", saved.get("do_ffc", False)):
            warnings.append("FFC was requested but did not execute; inspect the white image and report")
        metadata = {"version": 1, "white": str(white_path) if white_path else None,
                    "white_sha256": white_hash,
                    "do_ffc": bool(saved.get("do_ffc", do_ffc)), "ffc": opts.ffc.to_dict() if do_ffc else None,
                    "crop_rect": opts.crop_rect, "metrics": copy.deepcopy(report),
                    "warnings": warnings, "report": str(paths["report"]),
                    "output": str(reply.get("output") or paths["output"]) if opts.write_output else None}
        saved["python_api"] = metadata
        _write_json(model_path, saved, overwrite=True)
        fitted_backends = _backends(reply, self.backend)
        # The fit consumes a detection snapshot; its reply need not repeat the
        # earlier detector telemetry. Preserve that observed execution evidence.
        actual = {**detected.backends.actual, **fitted_backends.actual}
        diagnostics = {**fitted_backends.diagnostics,
                       "detection": copy.deepcopy(detected.backends.diagnostics)}
        return CalibrationModel(self, model_path, folder, metadata, copy.deepcopy(reply),
                                BackendInfo(self.backend, actual, diagnostics))

    def load_calibration(self, model_path: PathLike) -> CalibrationModel:
        self._check_open()
        path = _input(model_path, "saved model")
        saved = _json(path)
        pipeline = any(key in saved for key in ("correction", "gc_coeffs", "gc_model", "wb_diag"))
        legacy_correction = "feature_count" in saved and ("coefficients" in saved or "w1" in saved)
        if not isinstance(saved.get("kind"), str) or not (pipeline or legacy_correction):
            raise InvalidInputError("Not a ColorCorrect saved pipeline or legacy correction model")
        metadata = saved.get("python_api")
        if metadata is not None and (not isinstance(metadata, dict) or metadata.get("version") != 1):
            raise UnsupportedCapabilityError("Unsupported Python calibration metadata version")
        metadata = copy.deepcopy(metadata or {"version": 1, "do_ffc": bool(saved.get("do_ffc", False)), "metrics": None})
        if metadata.get("do_ffc") and not metadata.get("white"):
            metadata.setdefault("warnings", []).append("This legacy model requires an FFC white image; supply ApplyOptions(white=...)")
        return CalibrationModel(self, path, path.parent, metadata, {}, _backends({}, self.backend))

    def close(self) -> None:
        if not self._closed:
            self._closed = True
            self._engine.close()

    def __enter__(self) -> Client:
        self._start()
        return self

    def __exit__(self, *exc) -> None:
        self.close()


class CalibrationModel:
    """A fitted model with provenance, bound to the Client that loaded it."""
    def __init__(self, client: Client, path: Path, folder: Path, metadata: dict,
                 raw_reply: dict, backends: BackendInfo):
        self._client, self.path, self.folder = client, path, folder
        self._metadata = copy.deepcopy(metadata)
        self.raw_reply, self.backends = raw_reply, backends
        self._model_sha256 = _content_hash(path)

    def _check_model_identity(self) -> None:
        _input(self.path, "saved model")
        if _content_hash(self.path) != self._model_sha256:
            raise InvalidInputError("Saved model changed on disk; load_calibration() again to use the changed model")

    @property
    def metrics(self) -> dict | None:
        """Report metrics including CC-only validation; None for legacy models."""
        return copy.deepcopy(self._metadata.get("metrics"))

    @property
    def warnings(self) -> tuple[str, ...]:
        return tuple(self._metadata.get("warnings", []))

    @property
    def report_path(self) -> Path | None:
        value = self._metadata.get("report")
        return Path(value) if value else None

    @property
    def corrected_image(self) -> Path | None:
        value = self._metadata.get("output")
        return Path(value) if value else None

    def _apply_parameters(self, options: ApplyOptions | None) -> dict:
        self._client._check_open()
        self._check_model_identity()
        parameters = {key: copy.deepcopy(self._metadata[key]) for key in ("do_ffc", "ffc", "crop_rect") if self._metadata.get(key) is not None}
        if options is not None and not isinstance(options, ApplyOptions):
            raise InvalidInputError("options must be ApplyOptions")
        effective_options = options or ApplyOptions()
        if effective_options.do_ffc is None:
            effective_options = replace(effective_options, do_ffc=bool(parameters.get("do_ffc")))
        requested = effective_options.to_dict()
        parameters.update(requested)
        if parameters.get("fast_mode") and "combined_lut65_v1" not in self._client.capabilities:
            raise UnsupportedCapabilityError("This worker cannot use fast mode; update the native engine")
        if any(parameters.get(key) is True for key in ("do_gc", "do_wb", "do_cc")):
            saved = _json(self.path)
            available = {"do_gc": saved.get("gc_model") is not None or saved.get("gc_coeffs") is not None,
                         "do_wb": saved.get("wb_diag") is not None,
                         "do_cc": saved.get("correction") is not None or
                                  ("feature_count" in saved and ("coefficients" in saved or "w1" in saved))}
            for stage, present in available.items():
                if parameters.get(stage) is True and not present:
                    raise InvalidInputError(f"Requested {stage[3:].upper()} stage is absent from this saved model; fit it first or disable that stage")
        if parameters.get("do_ffc"):
            white = parameters.get("white") or self._metadata.get("white")
            if not white:
                raise InvalidInputError("This model requires an FFC white image; supply ApplyOptions(white=...) or explicitly disable FFC")
            parameters["white"] = _input(white, "FFC white")
            expected = self._metadata.get("white_sha256")
            if expected and not (options and options.white is not None) and _content_hash(parameters["white"]) != expected:
                raise InvalidInputError("FFC white image changed since calibration; restore it or explicitly supply ApplyOptions(white=...) for a new reference")
        return parameters

    def apply(self, images: PathLike | Sequence[PathLike], *, output_dir: PathLike,
              options: ApplyOptions | None = None, on_error: str = "raise") -> ApplyResult:
        self._client._check_open()
        _choice(on_error, ("raise", "continue"), "on_error")
        parameters = self._apply_parameters(options)
        try:
            items = [images] if isinstance(images, (str, os.PathLike)) else list(images)
        except TypeError as exc:
            raise InvalidInputError("images must be a path or a sequence of paths") from exc
        if not items:
            raise InvalidInputError("images must contain at least one path")
        paths, invalid = [], {}
        for index, image in enumerate(items):
            try:
                path = _input(image, "image")
            except InvalidInputError as exc:
                try:
                    path = Path(image).expanduser().resolve()
                except (TypeError, ValueError, OSError):
                    raise InvalidInputError(f"Invalid image path at index {index}: {image!r}") from exc
                invalid[index] = str(exc)
            paths.append(path)
        folder = _run_directory(output_dir, "applied")
        valid = [path for index, path in enumerate(paths) if index not in invalid]
        reply = self._client._request("apply", check=False, model=self.path, images=valid,
                                      output_dir=folder, **parameters) if valid else {"ok": False, "results": []}
        rows = reply.get("results")
        if not isinstance(rows, list) or len(rows) != len(valid) or any(not isinstance(row, dict) for row in rows):
            raise ProcessingError(reply.get("error") or "Worker returned incomplete batch outcomes", reply)
        iterator = iter(rows)
        outcomes = []
        for index, path in enumerate(paths):
            row = {"ok": False, "error": invalid[index]} if index in invalid else next(iterator)
            output = Path(row["output"]).resolve() if row.get("output") and parameters["write_output"] else None
            if row.get("ok") is True and parameters["write_output"] and (output is None or not output.is_file()):
                row = {**row, "ok": False, "error": "Worker reported success without a written output file"}
                output = None
            outcomes.append(ImageResult(path, row.get("ok") is True, output, row.get("error"),
                                        _warnings(row), _backends(row, self._client.backend)))
        warnings = list(_warnings(reply)) + [warning for row in outcomes for warning in row.warnings]
        result = ApplyResult(folder, tuple(outcomes), _backends(reply, self._client.backend), tuple(warnings), reply)
        if result.failures and on_error == "raise":
            raise BatchProcessingError(result)
        return result

    def export_lut(self, path: PathLike, *, size: int = 33, overwrite: bool = False) -> LUTResult:
        self._client._check_open()
        self._check_model_identity()
        _integer(size, "size", 2, 129)
        destination = Path(path).expanduser().resolve()
        _export_destination(destination, overwrite)
        reply = self._client._request("export_cube", model=self.path, path=destination, size=size)
        actual = _artifact(reply.get("path") or destination, "exported LUT", reply)
        warnings = list(_warnings(reply))
        if self._metadata.get("do_ffc"):
            warnings.append("LUT contains only the global transform; spatial FFC is excluded")
        return LUTResult(actual, size, tuple(warnings), reply)


def _export_destination(path: Path, overwrite: bool) -> None:
    if path.exists() and not overwrite:
        raise InvalidInputError(f"Destination exists: {path}; use overwrite=True to replace it")
    if path.exists() and not path.is_file():
        raise InvalidInputError(f"Destination is not a file: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)


__all__ = ["Client", "CalibrationModel", "ReferenceProfile", "DetectionOptions", "CalibrationOptions",
           "ApplyOptions", "FFCOptions", "NeuralNetworkOptions", "DetectionResult", "DetectedChart",
           "BackendInfo", "ImageResult", "ApplyResult", "LUTResult", "ColorCorrectError",
           "InvalidInputError", "MissingRuntimeError", "UnsupportedCapabilityError", "ProcessingError",
           "ProcessingTimeoutError", "ClosedClientError", "BatchProcessingError"]
