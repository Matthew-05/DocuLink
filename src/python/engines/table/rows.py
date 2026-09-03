"""Build true row bands and retain the source text-line bands used for merging."""
from __future__ import annotations

from statistics import median

from engines.table.regions import line_records


def _occupied_columns(record: dict, columns: list[dict]) -> list[int]:
    occupied: set[int] = set()
    for character in record["characters"]:
        if not str(character.get("char", "")).strip():
            continue
        center = float(character["x"]) + float(character["width"]) / 2
        for index, column in enumerate(columns):
            if column["x0"] <= center <= column["x1"]:
                occupied.add(index)
                break
    return sorted(occupied)


def detect_rows(
    page_geometry: dict,
    bounds: dict,
    horizontal_rulings: list[float],
    columns: list[dict],
    *,
    grid: bool = False,
) -> list[dict]:
    top = bounds["y"]
    bottom = top + bounds["height"]
    # `line_records` keeps any line that overlaps the region, so a caption sitting
    # just above the table came back as a member. Its centre is above the top edge,
    # which made the first band a sliver holding no text — and header detection,
    # which reads the first bands, then saw nothing.
    records = [
        record
        for record in line_records(page_geometry, bounds)
        if top <= (record["y0"] + record["y1"]) / 2 <= bottom
    ]
    ruled = [value for value in horizontal_rulings if top - 0.002 <= value <= bottom + 0.002]
    # Only a genuine grid may define rows by its rules. Financial statements
    # underline every subtotal, which easily clears three horizontal lines while
    # being emphasis, not structure — and letting those define the bands discarded
    # every line outside them, the period header first of all.
    if grid and len(ruled) >= 3:
        ruled = sorted(ruled)
        # Anchor the outer edges to the table so nothing above the first rule or
        # below the last one falls outside every row.
        # Stretch the outer edges only where text actually sits outside the rules,
        # so a header above the first rule is captured without inventing a blank
        # band on a bordered form that simply starts with its border.
        def _center(record: dict) -> float:
            return (record["y0"] + record["y1"]) / 2

        if any(top <= _center(record) < ruled[0] for record in records):
            ruled[0] = top
        if any(ruled[-1] < _center(record) <= bottom for record in records):
            ruled[-1] = bottom
        rows = []
        for index in range(len(ruled) - 1):
            y0, y1 = ruled[index], ruled[index + 1]
            text_lines = [
                {"y0": record["y0"], "y1": record["y1"]}
                for record in records
                if y0 <= (record["y0"] + record["y1"]) / 2 <= y1
            ]
            rows.append({
                "y0": y0, "y1": y1, "kind": "body", "textLines": text_lines,
                "merged": len(text_lines) > 1, "mergeConfidence": 1.0,
            })
        return rows
    if len(records) < 2:
        return []

    heights = [record["y1"] - record["y0"] for record in records]
    typical_height = max(0.001, median(heights))
    grouped: list[list[dict]] = []
    merge_confidences: list[float] = []
    for record in records:
        occupied = _occupied_columns(record, columns)
        previous = grouped[-1] if grouped else None
        previous_occupied = sorted({
            column
            for previous_record in (previous or [])
            for column in _occupied_columns(previous_record, columns)
        })
        gap = record["y0"] - previous[-1]["y1"] if previous else 1.0
        continuation = (
            previous is not None
            and occupied == [0]
            and any(index > 0 for index in previous_occupied)
            and gap <= typical_height * 0.9
        )
        if continuation:
            previous.append(record)
            proximity = max(0.0, 1.0 - max(0.0, gap) / typical_height)
            emptiness = (len(columns) - 1) / max(1, len(columns))
            merge_confidences[-1] = min(0.99, 0.6 + emptiness * 0.25 + proximity * 0.15)
        else:
            grouped.append([record])
            merge_confidences.append(0.0)

    centers = [sum((item["y0"] + item["y1"]) / 2 for item in group) / len(group) for group in grouped]
    edges = [top] + [(centers[index] + centers[index + 1]) / 2 for index in range(len(centers) - 1)] + [bottom]
    rows: list[dict] = []
    for index, group in enumerate(grouped):
        rows.append({
            "y0": edges[index], "y1": edges[index + 1], "kind": "body",
            "textLines": [{"y0": item["y0"], "y1": item["y1"]} for item in group],
            "merged": len(group) > 1, "mergeConfidence": merge_confidences[index],
        })
    return rows
