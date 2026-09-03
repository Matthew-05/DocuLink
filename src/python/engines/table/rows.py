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
) -> list[dict]:
    top = bounds["y"]
    bottom = top + bounds["height"]
    records = line_records(page_geometry, bounds)
    ruled = [value for value in horizontal_rulings if top - 0.002 <= value <= bottom + 0.002]
    if len(ruled) >= 3:
        ruled = sorted(ruled)
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
