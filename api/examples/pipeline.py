"""Runnable, path-based equivalent of the original package's run/predict workflow."""
from pathlib import Path
from colorcorrect import ColorCorrection, Config


def run_pipeline(chart: Path, images: list[Path], output_dir: Path, *,
                 executable: Path | None = None, root: Path | None = None):
    with ColorCorrection(Config(save_path=output_dir), executable=executable, root=root) as cc:
        fitted = cc.run(Image=chart)
        predictions = cc.predict_images(images=images)
        print(fitted.metrics)
        print(predictions.outputs)
        return fitted.path, predictions.outputs
