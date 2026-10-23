"""Small runnable workflows used by docs and native integration tests.

Call these functions with your own paths or run integration_sdk.py against the
source sample corpus. No optional Python dependencies are needed.
"""
from pathlib import Path
from colorcorrect import Client, ReferenceProfile, CalibrationOptions, ApplyOptions, DetectionResult


def calibrate_builtin(client: Client, chart: Path, output_dir: Path, *, detection: DetectionResult | None = None):
    return client.calibrate(chart, output_dir=output_dir, detection=detection,
                            options=CalibrationOptions(write_output=False))


def calibrate_custom(client: Client, chart: Path, reference_csv: Path, output_dir: Path, *, detection: DetectionResult | None = None):
    reference = ReferenceProfile.from_csv(reference_csv, chart="classic", color_space="lab",
                                          white_point="d50", order="row-major")
    return client.calibrate(chart, reference=reference, output_dir=output_dir, detection=detection,
                            options=CalibrationOptions(write_output=False))


def apply_batch(calibration, images, output_dir: Path):
    result = calibration.apply(images, output_dir=output_dir, options=ApplyOptions(output_format="jpg"))
    print("Corrected:", *result.outputs)
    return result


def reload_and_export(client: Client, model_path: Path, lut_path: Path):
    calibration = client.load_calibration(model_path)
    return calibration, calibration.export_lut(lut_path, size=17)
