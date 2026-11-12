"""Calibrate with the stable grid or opt into seeded random patch means.

Use the matching rebuilt worker. Set PYTHONPATH to the shipped api directory.
All outputs go into a new calibration subdirectory inside --folder.
"""
from pathlib import Path
import argparse
import json

from colorcorrect import Engine


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True, help="App resources containing models/ and refs/")
    parser.add_argument("--engine", type=Path, required=True, help="Matching rebuilt ccp-engine executable")
    parser.add_argument("--chart", type=Path, required=True)
    parser.add_argument("--folder", type=Path, required=True, help="Durable parent for unique run directories")
    parser.add_argument("--handshake", type=Path, help="Existing geometry for this exact chart image; otherwise detect")
    parser.add_argument("--white", type=Path, help="Matching blank field; enables FFC")
    parser.add_argument("--scene", type=Path, action="append", default=[], help="Optional image to apply; repeat as needed")
    sampling = parser.add_mutually_exclusive_group()
    sampling.add_argument("--fit-samples", type=int, help="Grid points per axis, 1..200; default 9")
    sampling.add_argument("--samples-per-patch", type=int, help="Exact random count, 1..4096; each patch yields one mean")
    parser.add_argument("--sampling-seed", type=int, help="Unsigned 32-bit seed; random mode only, default 0")
    args = parser.parse_args(argv)
    if args.sampling_seed is not None and args.samples_per_patch is None:
        parser.error("--sampling-seed requires --samples-per-patch")

    # Edit stage/fitting options here. GC and WB are fitted automatically.
    options = dict(model="rpcc2", do_gc=True, do_wb=True, do_cc=True,
                   cross_validate=True, phase2_teacher=False,
                   ffc={"method": "poly", "degree": 5, "bins": 50, "smooth": 5})
    if args.handshake is not None:
        options["handshake"] = args.handshake
    if args.samples_per_patch is not None:
        options["samples_per_patch"] = args.samples_per_patch
        if args.sampling_seed is not None:
            options["sampling_seed"] = args.sampling_seed
    elif args.fit_samples is not None:
        options["fit_samples"] = args.fit_samples

    with Engine(args.engine, root=args.root) as engine:
        fitted = engine.calibrate(args.chart, folder=args.folder, white=args.white, **options)
        report = fitted.report["report_json"]
        print(json.dumps({"model": str(fitted.model), "folder": str(fitted.folder),
                          "do_ffc": report["do_ffc"],
                          "fit_samples_per_axis": report.get("fit_samples_per_axis"),
                          "patch_sampling": report.get("patch_sampling"),
                          "ffc_effective_degree": report.get("ffc_effective_degree")}, indent=2))
        if args.scene:
            result = fitted.apply(args.scene, fitted.folder / "applied", output_format="png")
            print(json.dumps(result["results"], indent=2))

    # A later process uses Engine.apply(fitted.model, ...). Applying does not
    # resample chart patches. Explicitly re-enable FFC and its matching white.


if __name__ == "__main__":
    main()
