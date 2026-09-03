"""Derive table column bands from rulings or shared whitespace gutters."""
from __future__ import annotations

import math
import re
from statistics import median

from engines.table.regions import line_records


# Accounting layouts float a currency or sign marker in its own whitespace island,
# well clear of the amount it belongs to. It is a marker, never a column.
_MARKER_ONLY = re.compile(r"^[$€£¥%()]+$")


def _visible(characters: list[dict]) -> list[dict]:
    return [item for item in characters if str(item.get("char", "")).strip()]


def _cell_text(record: dict, x0: float, x1: float) -> str:
    pieces = sorted(
        (float(item["x"]), str(item["char"]))
        for item in _visible(record["characters"])
        if x0 <= float(item["x"]) + float(item["width"]) / 2 <= x1
    )
    return "".join(char for _x, char in pieces).strip()


def _uncrossed(boundaries: list[float], records: list[dict]) -> list[float]:
    """Drop boundaries that cut through text in a meaningful share of rows.

    A gutter derived from one leading row can land inside the label column of
    every other row — the whitespace is real on that row and nowhere else.
    Splitting there quietly halves values ("Cull" | "um Development, Inc."), so a
    boundary has to survive the rest of the table, not just the row that proposed
    it. A rare overflowing cell is tolerated; systematic crossing is not.
    """
    if not records:
        return boundaries
    kept: list[float] = []
    for boundary in boundaries:
        crossing = sum(
            any(
                float(item["x"]) < boundary < float(item["x"]) + float(item["width"])
                for item in _visible(record["characters"])
            )
            for record in records
        )
        # One row may legitimately overflow its cell, and on a three-row table that
        # is already a third of the evidence — so always tolerate a single crosser.
        if crossing <= max(1, len(records) * 0.15):
            kept.append(boundary)
    return kept


def _coalesce(boundaries: list[float], left: float, right: float, records: list[dict]) -> list[float]:
    """Merge bands that hold no data, or hold only a currency/sign marker.

    Both merges remove a boundary, never a cell, so every character stays inside
    some column. Over-segmentation here is what produced empty spreadsheet
    columns and split `$` away from the amount it belongs to.
    """
    while boundaries:
        edges = [left] + boundaries + [right]
        texts = [
            [_cell_text(record, edges[index], edges[index + 1]) for record in records]
            for index in range(len(edges) - 1)
        ]
        for index, column in enumerate(texts):
            filled = [value for value in column if value]
            carried_by = [row for row, value in enumerate(column) if value]
            # A band no row fills, or that only the top row fills, holds no data:
            # a wide corridor's centre landing inside a header label ("P. O. #" cut
            # into "P." and "O. #") produces exactly this. Collapsing it can merge
            # two header labels; it can never lose a value.
            if not filled or carried_by == [0]:
                # Collapse an empty band onto one boundary at its midpoint; at
                # either table edge the inner boundary simply goes away.
                if index == 0:
                    boundaries = boundaries[1:]
                elif index == len(texts) - 1:
                    boundaries = boundaries[:-1]
                else:
                    midpoint = (edges[index] + edges[index + 1]) / 2
                    boundaries = boundaries[: index - 1] + [midpoint] + boundaries[index + 1 :]
                break
            if index < len(texts) - 1 and all(_MARKER_ONLY.match(value) for value in filled):
                boundaries = boundaries[:index] + boundaries[index + 1 :]
                break
        else:
            break
    return boundaries


def detect_columns(page_geometry: dict, bounds: dict, vertical_rulings: list[float]) -> list[dict]:
    left = bounds["x"]
    right = left + bounds["width"]
    ruled = [value for value in vertical_rulings if left - 0.002 <= value <= right + 0.002]
    if len(ruled) >= 3:
        ruled = sorted(ruled)
        return [{"x0": ruled[index], "x1": ruled[index + 1]} for index in range(len(ruled) - 1)]

    records = line_records(page_geometry, bounds)
    chars = [character for record in records for character in _visible(record["characters"])]
    widths = [float(item["width"]) for item in chars if float(item.get("width", 0)) > 0]
    if len(records) < 2 or not widths:
        return []
    minimum_gap = max(0.008, median(widths) * 1.8)
    gaps_by_line: list[list[tuple[float, float]]] = []
    for record in records:
        ordered = sorted(_visible(record["characters"]), key=lambda item: float(item["x"]))
        gaps: list[tuple[float, float]] = []
        occupied_right = float(ordered[0]["x"]) + float(ordered[0]["width"]) if ordered else left
        for item in ordered[1:]:
            item_left = float(item["x"])
            if item_left - occupied_right >= minimum_gap:
                gaps.append((occupied_right, item_left))
            occupied_right = max(occupied_right, item_left + float(item["width"]))
        gaps_by_line.append(gaps)

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

    # Sparse columns (for example P.O. number or aging buckets) may be empty in
    # most body rows, causing adjacent whitespace corridors to collapse into one.
    # The first few table rows are normally headers or representative data rows, so
    # their explicit segmentation is supplemental evidence — added to the shared
    # gutters, never substituted for them. Trusting one row outright let a `$`
    # marker row fragment every amount column and dropped real gutters that row
    # happened not to show.
    leading_gaps = max(gaps_by_line[: min(3, len(gaps_by_line))], key=len, default=[])
    leading_values: list[float] = []
    for start, end in leading_gaps:
        candidate = (start + end) / 2
        if all(abs(candidate - value) >= minimum_gap for value in boundaries):
            boundaries.append(candidate)
            leading_values.append(candidate)
    boundaries = sorted(_uncrossed(boundaries, records))

    # Fold currency markers and dead bands away first: until that is done, the
    # boundary splitting `$` from its amount looks like a rival for the real
    # label/amount gutter and would win the placement contest below.
    boundaries = _coalesce(boundaries, left, right, records)

    # Two surviving candidates for one gutter: the vote's centre drifts toward the
    # middle of a wide corridor when a column is empty, which can land inside a
    # header label ("P. O. #" cut into "P." and "O. #"). The leading row delimits
    # that gutter explicitly, so its placement wins.
    resolved: list[float] = []
    for value in boundaries:
        if resolved and value - resolved[-1] <= minimum_gap * 3:
            if value in leading_values and resolved[-1] not in leading_values:
                resolved[-1] = value
            continue
        resolved.append(value)
    boundaries = resolved

    boundaries = _coalesce(boundaries, left, right, records)
    edges = [left] + boundaries + [right]
    return [{"x0": edges[index], "x1": edges[index + 1]} for index in range(len(edges) - 1)] if len(edges) >= 3 else []
