"""Discover ruled grids and dense aligned-text table regions."""
from __future__ import annotations

from statistics import median

from engines.ocr_engine import _line_bounds, _line_groups


def line_records(page_geometry: dict, bounds: dict | None = None) -> list[dict]:
    records: list[dict] = []
    for characters in _line_groups(page_geometry):
        line_bounds = _line_bounds(characters)
        if line_bounds is None:
            continue
        x0, y0, x1, y1 = line_bounds
        if bounds is not None and (
            x1 < bounds["x"] or x0 > bounds["x"] + bounds["width"]
            or y1 < bounds["y"] or y0 > bounds["y"] + bounds["height"]
        ):
            continue
        records.append({"x0": x0, "y0": y0, "x1": x1, "y1": y1, "characters": characters})
    ordered = sorted(records, key=lambda item: ((item["y0"] + item["y1"]) / 2, item["x0"]))
    visual: list[dict] = []
    for record in ordered:
        previous = visual[-1] if visual else None
        if previous is not None:
            overlap = max(0.0, min(previous["y1"], record["y1"]) - max(previous["y0"], record["y0"]))
            smaller_height = min(previous["y1"] - previous["y0"], record["y1"] - record["y0"])
            previous_center = (previous["y0"] + previous["y1"]) / 2
            current_center = (record["y0"] + record["y1"]) / 2
            center_tolerance = max(previous["y1"] - previous["y0"], record["y1"] - record["y0"]) * 0.55
            same_visual_row = (
                (smaller_height > 0 and overlap / smaller_height >= 0.4)
                or abs(previous_center - current_center) <= center_tolerance
            )
            if same_visual_row:
                previous["x0"] = min(previous["x0"], record["x0"])
                previous["y0"] = min(previous["y0"], record["y0"])
                previous["x1"] = max(previous["x1"], record["x1"])
                previous["y1"] = max(previous["y1"], record["y1"])
                previous["characters"].extend(record["characters"])
                continue
        visual.append(record)
    return visual


def _segment_count(record: dict) -> int:
    chars = sorted(
        [item for item in record["characters"] if str(item.get("char", "")).strip()],
        key=lambda item: float(item["x"]),
    )
    if len(chars) < 2:
        return 1 if chars else 0
    widths = [float(item["width"]) for item in chars if float(item.get("width", 0)) > 0]
    threshold = max(0.012, (median(widths) if widths else 0.005) * 2.5)
    count = 1
    right = float(chars[0]["x"]) + float(chars[0]["width"])
    for character in chars[1:]:
        left = float(character["x"])
        if left - right >= threshold:
            count += 1
        right = max(right, left + float(character["width"]))
    return count


def discover_regions(
    page_geometry: dict,
    vertical_rulings: list[float],
    horizontal_rulings: list[float],
) -> list[dict]:
    candidates: list[dict] = []
    if len(vertical_rulings) >= 3 and len(horizontal_rulings) >= 3:
        x0, x1 = min(vertical_rulings), max(vertical_rulings)
        y0, y1 = min(horizontal_rulings), max(horizontal_rulings)
        candidates.append({
            "bounds": {"x": x0, "y": y0, "width": x1 - x0, "height": y1 - y0},
            "evidence": "ruled",
            "confidence": 0.96,
        })

    records = line_records(page_geometry)
    if not records:
        return candidates
    heights = [record["y1"] - record["y0"] for record in records]
    max_gap = max(0.018, median(heights) * 2.5)
    groups: list[list[dict]] = []
    for record in records:
        if _segment_count(record) < 2:
            continue
        if not groups or record["y0"] - groups[-1][-1]["y1"] > max_gap:
            groups.append([record])
        else:
            groups[-1].append(record)
    for group in groups:
        if len(group) < 3:
            continue
        # Three-row whitespace tables are useful, but a short address/date block
        # can look tabular too. Require broader column alignment for this smallest
        # accepted shape; longer blocks already provide enough repeated evidence.
        if len(group) == 3 and median(_segment_count(item) for item in group) < 4:
            continue
        # A single OCR artifact near a page edge must not widen the entire table.
        # Aligned rows normally agree on their outer columns, so median extents are
        # a stable region estimate while still allowing uneven cell text lengths.
        x0 = median(item["x0"] for item in group)
        x1 = median(item["x1"] for item in group)
        y0 = min(item["y0"] for item in group)
        y1 = max(item["y1"] for item in group)
        bounds = {
            "x": max(0.0, x0 - 0.004),
            "y": max(0.0, y0 - median(heights) * 0.4),
            "width": min(1.0, x1 + 0.004) - max(0.0, x0 - 0.004),
            "height": min(1.0, y1 + median(heights) * 0.4) - max(0.0, y0 - median(heights) * 0.4),
        }
        if any(_overlap_ratio(bounds, candidate["bounds"]) > 0.5 for candidate in candidates):
            continue
        candidates.append({"bounds": bounds, "evidence": "whitespace", "confidence": min(0.88, 0.58 + len(group) * 0.04)})
    return sorted(candidates, key=lambda item: (item["bounds"]["y"], item["bounds"]["x"]))


def _overlap_ratio(first: dict, second: dict) -> float:
    left = max(first["x"], second["x"])
    top = max(first["y"], second["y"])
    right = min(first["x"] + first["width"], second["x"] + second["width"])
    bottom = min(first["y"] + first["height"], second["y"] + second["height"])
    intersection = max(0.0, right - left) * max(0.0, bottom - top)
    area = min(first["width"] * first["height"], second["width"] * second["height"])
    return intersection / area if area > 0 else 0.0
