"""Familiar run/predict workflow backed by the typed native SDK.

The naming follows ColorCorrectionPipeline, but this is not a drop-in replacement:
inputs are image paths, results are typed artifacts, and failures raise exceptions.
"""
from __future__ import annotations

from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import Callable, Mapping, Sequence

from colorcorrect_sdk import (
    ApplyOptions, ApplyResult, CalibrationModel, CalibrationOptions, Client,
    DetectionResult, InvalidInputError, PathLike, ReferenceProfile,
)


@dataclass(frozen=True, kw_only=True)
class Config:
    """Pipeline switches and an explicit artifact directory.

    ``do_ffc=None`` enables FFC when a White_Image is supplied. Advanced settings
    use the documented CalibrationOptions dataclass, not unvalidated dictionaries.
    Each run creates its own directory below save_path, without overwriting files.
    """
    save_path: PathLike
    do_ffc: bool | None = None
    do_gc: bool = True
    do_wb: bool = True
    do_cc: bool = True
    check_saturation: bool = True
    options: CalibrationOptions = field(default_factory=CalibrationOptions)

    def __post_init__(self):
        if not isinstance(self.save_path, (str, Path)) or not str(self.save_path).strip():
            raise InvalidInputError("Config.save_path must be an explicit output directory")
        if not isinstance(self.options, CalibrationOptions):
            raise InvalidInputError("Config.options must be CalibrationOptions")
        for key in ("do_ffc", "do_gc", "do_wb", "do_cc", "check_saturation"):
            value = getattr(self, key)
            if not (key == "do_ffc" and value is None) and type(value) is not bool:
                raise InvalidInputError(f"Config.{key} must be bool")
        self.calibration_options().to_dict()

    def calibration_options(self) -> CalibrationOptions:
        return replace(self.options, do_ffc=self.do_ffc, do_gc=self.do_gc,
                       do_wb=self.do_wb, do_cc=self.do_cc, do_sat=self.check_saturation)


class ColorCorrection:
    """Fit once with run(), then predict_image()/predict_images().

    Use as a context manager or explicitly close(). Automatic acceleration is the default.
    Config and method names resemble ColorCorrectionPipeline; algorithms, models,
    return values and advanced options are the ColorCorrect Studio SDK contract.
    """
    def __init__(self, config: Config | None = None, *, executable: PathLike | None = None,
                 root: PathLike | None = None, backend: str = "auto", timeout: float = 600,
                 progress: Callable[[str], None] | None = None):
        if config is not None and not isinstance(config, Config):
            raise InvalidInputError("config must be Config")
        self.config = config
        self.client = Client(executable, root=root, backend=backend, timeout=timeout, progress=progress)
        self.model: CalibrationModel | None = None

    def run(self, Image: PathLike, White_Image: PathLike | None = None, *,
            config: Config | None = None, reference: ReferenceProfile | None = None,
            detection: DetectionResult | None = None,
            references: Mapping[str, ReferenceProfile] | None = None) -> CalibrationModel:
        """Detect and calibrate an image; return saved model, metrics and paths.

        Supplying config here sets the default output directory for predictions
        after a successful fit. A failed fit leaves the previous model intact.
        """
        selected = config if config is not None else self.config
        if not isinstance(selected, Config):
            raise InvalidInputError("Supply Config(save_path=...) to ColorCorrection or run")
        fitted = self.client.calibrate(Image, white=White_Image, output_dir=selected.save_path,
            options=selected.calibration_options(), reference=reference,
            detection=detection, references=references)
        self.model, self.config = fitted, selected
        return fitted

    def load_model(self, path: PathLike) -> CalibrationModel:
        """Load a Studio model. Original Python-package pickle models are unsupported."""
        fitted = self.client.load_calibration(path)
        self.model = fitted
        return fitted

    def _prediction_output(self, output_dir: PathLike | None) -> PathLike:
        if self.model is None:
            raise InvalidInputError("Call run(...) or load_model(...) before prediction")
        if output_dir is not None:
            return output_dir
        if self.config is not None:
            return self.config.save_path
        raise InvalidInputError("Supply output_dir=... or construct with Config(save_path=...)")

    def predict_image(self, Image: PathLike, *, output_dir: PathLike | None = None,
                      options: ApplyOptions | None = None) -> ApplyResult:
        """Apply the fitted stages to one image, returning the same shape as batches."""
        destination = self._prediction_output(output_dir)
        return self.model.apply(Image, output_dir=destination, options=options)

    def predict_images(self, images: Sequence[PathLike], *, output_dir: PathLike | None = None,
                       options: ApplyOptions | None = None, on_error: str = "raise") -> ApplyResult:
        """Apply sequentially in the persistent worker; expose every batch outcome."""
        destination = self._prediction_output(output_dir)
        return self.model.apply(images, output_dir=destination, options=options, on_error=on_error)

    def close(self) -> None:
        self.client.close()

    def __enter__(self) -> ColorCorrection:
        self.client.__enter__()
        return self

    def __exit__(self, *exc) -> None:
        self.close()


__all__ = ["ColorCorrection", "Config"]
