"""Assert reviewed chart counts and canonical corner-cell order, across providers.

Derived rotations use exact byte rotations in the benchmark host; they are
synthetic augmentations. No independent color values or chart corners invented.
"""
from __future__ import annotations
import argparse
from collections import Counter
import hashlib
import itertools
import json
import math
import os
from pathlib import Path
import shutil
import sys



def chart_type(chart: dict) -> str:
    value = chart.get("chart_type")
    if not isinstance(value, str):
        return "invalid"
    return {"classic_4x6": "classic", "spyder_4x6": "spyder", "digital_sg_10x14": "digital-sg"}.get(value, value)


TEMPLATE_DIMENSIONS = {"classic": (4, 6), "spyder": (4, 6), "digital-sg": (10, 14)}


def centers(chart: dict, size: int) -> list[tuple[float, float]]:
    if not isinstance(size, (int, float)) or not math.isfinite(size) or size <= 0:
        raise ValueError("invalid model canvas size")
    values = []
    for cell in chart["patch_cells"]:
        points = list(cell.values()) if isinstance(cell, dict) else cell
        if not isinstance(points, list) or len(points) != 4:
            raise ValueError("each indexed cell must contain four corners")
        xy = [(point["x"], point["y"]) if isinstance(point, dict) else tuple(point) for point in points]
        if any(len(point) != 2 or any(not isinstance(value, (int, float)) or not math.isfinite(value) for value in point) for point in xy):
            raise ValueError("indexed cell contains malformed/nonfinite geometry")
        values.append((sum(point[0] for point in xy) / (4 * size), sum(point[1] for point in xy) / (4 * size)))
    return values


def assert_cell_order(chart: dict, size: int, required: dict, derived_rotation: int) -> dict:
    """Use reviewed canonical corner identities, never infer orientation from a filename."""
    base = {"expected_identity": required.get("identity"), "expected_template": required.get("detected_template"),
            "observed_template": chart_type(chart), "expected_dimensions": [required.get("rows"), required.get("columns")]}
    try:
        rows, columns = required["rows"], required["columns"]
        if type(rows) is not int or type(columns) is not int or min(rows, columns) < 2:
            raise ValueError("reviewed rows/columns must be integers >=2")
        expected = required["canonical_corner_quadrants"]
        if not isinstance(expected, list) or sorted(expected) != [0, 1, 2, 3] or any(type(value) is not int for value in expected):
            raise ValueError("reviewed canonical_corner_quadrants must be a permutation of 0,1,2,3")
        if derived_rotation not in (0, 90, 180, 270):
            raise ValueError("only exact right-angle clockwise rotations are supported")
        if chart_type(chart) != required["detected_template"]:
            raise ValueError("detected template does not match reviewed chart")
        if TEMPLATE_DIMENSIONS.get(chart_type(chart)) != (rows, columns):
            raise ValueError("detected fixed template dimensions do not match reviewed chart")
        if chart.get("rows", rows) != rows or chart.get("cols", columns) != columns or chart.get("columns", columns) != columns:
            raise ValueError("explicit detector dimensions contradict reviewed chart")
        kind = chart.get("kind", chart_type(chart))
        kind = {"Classic": "classic", "Passport": "classic", "Spyder": "spyder", "DigitalSg": "digital-sg",
                "passport": "classic"}.get(kind, kind)
        if kind != chart_type(chart):
            raise ValueError("detector kind and template contradict each other")
        values = centers(chart, size)
        if len(values) != rows * columns:
            raise ValueError(f"grid must contain {rows * columns} indexed cells, got {len(values)}")
    except (KeyError, TypeError, ValueError) as error:
        return {**base, "passed": False, "error": str(error)}
    midpoint = (sum(x for x, _ in values) / len(values), sum(y for _, y in values) / len(values))
    # Quadrants clockwise: top-left0, top-right1, bottom-right2, bottom-left3.
    expected = [(value + derived_rotation // 90) % 4 for value in expected]
    observed = []
    indices = [0, columns - 1, (rows - 1) * columns, rows * columns - 1]
    for index in indices:
        x, y = values[index]
        if abs(x - midpoint[0]) < 1e-10 or abs(y - midpoint[1]) < 1e-10:
            return {**base, "passed": False, "error": "ambiguous/degenerate canonical corner quadrant"}
        observed.append((0 if x < midpoint[0] else 1) if y < midpoint[1] else (3 if x < midpoint[0] else 2))
    return {**base, "passed": observed == expected, "corner_cell_indices": indices,
            "expected_quadrants": expected, "observed_quadrants": observed,
            "dimensions_basis": "fixed detector template plus indexed cell count; explicit dimensions checked when supplied",
            "basis": "structured reviewed canonical corner identities in TL,TR,BL,BR cell order; quadrant comparison only"}


def match_reviewed_charts(result: dict, required: list[dict], rotation: int) -> dict:
    charts = result.get("charts", [])
    if not isinstance(charts, list) or len(charts) != len(required):
        return {"passed": False, "error": "chart count differs from reviewed corpus", "checks": []}
    if not required:
        return {"passed": True, "checks": [], "matching_basis": "reviewed no-chart negative"}
    best = None
    # Chart score/order can change across providers. Match identities jointly so
    # two different expected faces cannot both consume one detected chart.
    for indices in itertools.permutations(range(len(charts))):
        checks = [{"observed_chart_index": index, **assert_cell_order(charts[index], result.get("size"), expected, rotation)}
                  for expected, index in zip(required, indices)]
        score = sum(check["passed"] for check in checks)
        if best is None or score > best[0]:
            best = (score, checks)
        if score == len(required):
            break
    return {"passed": best[0] == len(required), "checks": best[1],
            "matching_basis": "one-to-one permutation matching by template, dimensions and reviewed canonical orientation"}


def compare_cells(baseline: dict, candidate: dict) -> dict:
    left = baseline.get("charts", [])
    right = candidate.get("charts", [])
    if len(left) != len(right):
        return {"matched": False, "reason": "chart count differs"}
    best = None
    for permutation in itertools.permutations(right):
        if any(chart_type(a) != chart_type(b) for a, b in zip(left, permutation)):
            continue
        differences = []
        for a, b in zip(left, permutation):
            ac, bc = centers(a, baseline["size"]), centers(b, candidate["size"])
            if len(ac) != len(bc):
                break
            differences.extend(math.dist(x, y) * 1280 for x, y in zip(ac, bc))
        else:
            score = sum(differences)
            if best is None or score < best[0]:
                best = (score, differences)
    if best is None:
        return {"matched": False, "reason": "chart types or indexed cell counts differ"}
    distances = best[1]
    return {"matched": True, "max_indexed_cell_center_delta_1280_canvas_px": max(distances, default=0),
            "mean_indexed_cell_center_delta_1280_canvas_px": sum(distances) / len(distances) if distances else 0,
            "basis": "same-photo backend equivalence; CPU geometry is not independent ground truth"}
