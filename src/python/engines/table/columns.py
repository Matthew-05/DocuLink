"""Derive table column bands from rulings or shared whitespace gutters."""
from __future__ import annotations

import math
from statistics import median

from engines.table.regions import line_records


def detect_columns(page_geometry: dict, bounds: dict, vertical_rulings: list[float]) -> list[dict]:
    left = bounds["x"]
    right = left + bounds["width"]
    ruled = [value for value in vertical_rulings if left - 0.002 <= value <= right + 0.002]
    if len(ruled) >= 3:
        ruled = sorted(ruled)
        return [{"x0": ruled[index], "x1": ruled[index + 1]} for index in range(len(ruled) - 1)]

    records = line_records(page_geometry, bounds)
    chars = [
        character
        for record in records
        for character in record["characters"]
        if str(character.get("char", "")).strip()
    ]
    widths = [float(item["width"]) for item in chars if float(item.get("width", 0)) > 0]
    if len(records) < 2 or not widths:
        return []
    minimum_gap = max(0.008, median(widths) * 1.8)
    gaps_by_line: list[list[tuple[float, float]]] = []
    for record in records:
        ordered = sorted(
            [item for item in record["characters"] if str(item.get("char", "")).strip()],
            key=lambda item: float(item["x"]),
        )
        gaps: list[tuple[float, float]] = []
        occupied_right = float(ordered[0]["x"]) + float(ordered[0]["width"]) if ordered else left
        for item in ordered[1:]:
            item_left = float(item["x"])
            if item_left - occupied_right >= minimum_gap:
                gaps.append((occupied_right, item_left))
            occupied_right = max(occupied_right, item_left + float(item["width"]))
        gaps_by_line.append(gaps)

    # Sparse columns (for example P.O. number or aging buckets) may be empty in
    # most body rows, causing adjacent whitespace corridors to collapse into one.
    # The first few table rows are normally headers or representative data rows;
    # retain the strongest of their explicit segmentations as supplemental evidence.
    leading_gaps = max(gaps_by_line[: min(3, len(gaps_by_line))], key=len, default=[])
    leading_boundaries = [(start + end) / 2 for start, end in leading_gaps]

    required = max(2, math.ceil(len(records) * 0.45))
    samples: list[float] = []
    for index in range(1, 320):
        position = left + bounds["width"] * index / 320
        support = sum(any(start <= position <= end for start, end in gaps) for gaps in gaps_by_line)
        if support >= required:
            samples.append(position)
    regions: list[list[float]] = []
    for sample in samples:
        if not regions or sample - regions[-1][-1] > bounds["width"] / 250:
            regions.append([sample])
        else:
            regions[-1].append(sample)
    boundaries = [(region[0] + region[-1]) / 2 for region in regions if region[-1] - region[0] >= minimum_gap * 0.4]
    if len(leading_boundaries) > len(boundaries):
        boundaries = leading_boundaries
    edges = [left] + boundaries + [right]
    return [{"x0": edges[index], "x1": edges[index + 1]} for index in range(len(edges) - 1)] if len(edges) >= 3 else []
