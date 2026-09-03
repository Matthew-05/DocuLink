"""Discover ruled grids and dense aligned-text table regions."""
from __future__ import annotations

from statistics import median

from engines.text_lines import line_bounds, line_groups


# How far outside a table a short line may sit and still count as its caption,
# as a fraction of the page. A section caption is outdented by one indent step
# and sits on the next line; anything further belongs to something else.
_CAPTION_REACH = 0.03


def line_records(page_geometry: dict, bounds: dict | None = None) -> list[dict]:
    records: list[dict] = []
    for characters in line_groups(page_geometry):
        measured = line_bounds(characters)
        if measured is None:
            continue
        x0, y0, x1, y1 = measured
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
            larger_height = max(previous["y1"] - previous["y0"], record["y1"] - record["y0"])
            previous_center = (previous["y0"] + previous["y1"]) / 2
            current_center = (record["y0"] + record["y1"]) / 2
            # Overlap has to dominate. A sparse band (for example a period header
            # sitting over the first data line) overlaps the line below it by about
            # half a glyph height, which the previous 0.4-or-close-centers rule
            # merged away, destroying the header before columns were ever measured.
            # Fragments of one visual row overlap almost completely instead.
            # Mirrored in web/apps/document-viewer/src/services/table-extractor.ts
            # (shouldMergeRows) — keep the two in step.
            same_visual_row = (
                overlap / smaller_height >= 0.70
                if smaller_height > 0
                else abs(previous_center - current_center) <= larger_height * 0.25
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


def _segment_spans(record: dict) -> list[tuple[float, float]]:
    """The whitespace-separated runs on one visual line, as (start, end)."""
    chars = sorted(
        [item for item in record["characters"] if str(item.get("char", "")).strip()],
        key=lambda item: float(item["x"]),
    )
    if not chars:
        return []
    widths = [float(item["width"]) for item in chars if float(item.get("width", 0)) > 0]
    threshold = max(0.012, (median(widths) if widths else 0.005) * 2.5)
    spans: list[list[float]] = [[float(chars[0]["x"]), float(chars[0]["x"]) + float(chars[0]["width"])]]
    for character in chars[1:]:
        left = float(character["x"])
        if left - spans[-1][1] >= threshold:
            spans.append([left, left + float(character["width"])])
        else:
            spans[-1][1] = max(spans[-1][1], left + float(character["width"]))
    return [(start, end) for start, end in spans]


def _segment_gutters(record: dict) -> list[tuple[float, float]]:
    spans = _segment_spans(record)
    return [(spans[index][1], spans[index + 1][0]) for index in range(len(spans) - 1)]


def _segment_count(record: dict) -> int:
    return len(_segment_spans(record))


def _continues_block(group: list[dict], record: dict) -> bool:
    """Does this line share the block's column alignment?

    Used only to decide whether a section that follows a caption belongs to the
    table above it. Justified prose also breaks into runs, so run *count* proves
    nothing — the runs have to land on the columns the block already established.
    """
    # Compare gutters, not run starts. A report centres its header labels over the
    # columns, so "Type"/"Date" begin nowhere near the values beneath them — but
    # the whitespace corridors between them line up, which is what a column *is*.
    member_gutters = [gutter for member in group for gutter in _segment_gutters(member)]
    matches = 0
    for start, end in _segment_gutters(record):
        for other_start, other_end in member_gutters:
            overlap = min(end, other_end) - max(start, other_start)
            # Score against the wider corridor. Against the narrower one, the huge
            # gap in a report's title block ("3:13 PM      Door Works") swallows any
            # column gutter and the title joins the table.
            if overlap > 0 and overlap >= max(end - start, other_end - other_start) * 0.5:
                matches += 1
                break
    return matches >= 2


def _supported_edge(records: list[dict], key: str) -> float:
    """Keep repeated outdents without letting one stray glyph widen a table."""
    rightmost = key == "x1"
    starts = sorted((float(record[key]) for record in records), reverse=rightmost)
    if len(starts) < 2:
        return starts[0]

    character_widths = [
        float(character["width"])
        for record in records
        for character in record["characters"]
        if str(character.get("char", "")).strip()
        and float(character.get("width", 0)) > 0
    ]
    tolerance = max(0.003, (median(character_widths) if character_widths else 0.005) * 1.25)

    # A genuine edge normally recurs (section starts, totals, an aligned value
    # column). Pick the outermost such cluster. Requiring two rows preserves the
    # protection against a single OCR speck or page-edge annotation, while a
    # median over every row — the previous rule for the right edge — sliced the
    # last column off any table whose rows end at different places.
    for start in starts:
        peers = [value for value in starts if abs(value - start) <= tolerance]
        if len(peers) >= 2:
            return median(peers)
    return median(starts)


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
    widest_line = max((record["x1"] - record["x0"] for record in records), default=1.0)
    groups: list[list[dict]] = []
    # Section captions ("Cost of sales:") hold one segment, so they cannot vote on
    # the block's shape — but they are lines of the table, and `detect_rows` will
    # make rows of them. Carry them alongside each group so the bounds reach them;
    # measuring the edges from members alone clipped "Gross margin:" to "ss margin:".
    captions: list[list[dict]] = []
    pending: list[dict] = []
    previous_bottom: float | None = None
    for record in records:
        if _segment_count(record) < 2:
            if record["x1"] - record["x0"] < widest_line * 0.6:
                pending.append(record)
                if previous_bottom is not None:
                    # Bridge the gap: measuring the next row from the last member
                    # made every caption look like the end of the table, splitting
                    # one statement into three headerless fragments.
                    previous_bottom = max(previous_bottom, record["y1"])
            else:
                # A full-width line is prose, and prose ends the block.
                pending = []
            continue
        bridged = bool(groups) and previous_bottom is not None and groups[-1][-1]["y1"] < previous_bottom
        if (
            not groups
            or previous_bottom is None
            or record["y0"] - previous_bottom > max_gap
            or (bridged and not _continues_block(groups[-1], record))
        ):
            groups.append([record])
            captions.append([item for item in pending if record["y0"] - item["y1"] <= max_gap])
        else:
            groups[-1].append(record)
            captions[-1].extend(pending)
        pending = []
        previous_bottom = record["y1"]
    for index, group in enumerate(groups):
        if len(group) < 3:
            continue
        # Three-row whitespace tables are useful, but a short address/date block
        # can look tabular too. Require broader column alignment for this smallest
        # accepted shape; longer blocks already provide enough repeated evidence.
        if len(group) == 3 and median(_segment_count(item) for item in group) < 4:
            continue
        # Measure the members' own edges first and admit only captions that sit
        # within an indent step of them. Comparing against the raw minimum would
        # anchor the test to whatever OCR speck starts furthest left, which is
        # exactly the noise the supported-edge rule exists to ignore.
        member_left = _supported_edge(group, "x0")
        member_right = _supported_edge(group, "x1")
        # A caption is a label, not a sentence: "Operating expenses for 2025, 2024
        # and 2023 were as follows" is an intro line and belongs above the table,
        # not in its header.
        admitted = [
            item
            for item in captions[index]
            if item["x0"] >= member_left - _CAPTION_REACH
            and item["x1"] <= member_right + _CAPTION_REACH
            and item["x1"] - item["x0"] <= (member_right - member_left) * 0.4
        ]
        covered = group + admitted
        y0 = min(item["y0"] for item in group)
        y1 = max(item["y1"] for item in group)
        # A caption may pull the edge out to itself — that is how a single-line
        # period super-header ("2025") joins the table it labels — but only across
        # a real line gap. On a noisy scan, line heights balloon to a tenth of the
        # page, so the reach is capped in page terms too: a short line further than
        # this is a speck or someone else's heading, not this table's caption.
        reach = min(median(heights), _CAPTION_REACH)
        extending = True
        while extending:
            extending = False
            for item in admitted:
                if item["y0"] < y0 and y0 - item["y1"] <= reach:
                    y0 = item["y0"]
                    extending = True
                elif item["y1"] > y1 and item["y0"] - y1 <= reach:
                    y1 = item["y1"]
                    extending = True
        top = max(0.0, y0 - median(heights) * 0.4)
        bottom = min(1.0, y1 + median(heights) * 0.4)
        # Keep repeated outdents that represent higher hierarchy levels, while a
        # single OCR artifact near a page edge must not widen the entire table.
        x0 = _supported_edge(covered, "x0")
        x1 = _supported_edge(covered, "x1")
        bounds = {
            "x": max(0.0, x0 - 0.004),
            "y": top,
            "width": min(1.0, x1 + 0.004) - max(0.0, x0 - 0.004),
            "height": bottom - top,
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
