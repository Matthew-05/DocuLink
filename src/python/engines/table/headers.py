"""Detect a leading header band and merge its label text by column."""
from __future__ import annotations

import re


_NUMBER = re.compile(r"^[\s($+\-]*[\d,.%]+[)\s]*$")


def _cell_text(page_geometry: dict, column: dict, rows: list[dict]) -> str:
    pieces: list[tuple[int, float, str]] = []
    row_top = min(row["y0"] for row in rows)
    row_bottom = max(row["y1"] for row in rows)
    for character in page_geometry.get("characters", []):
        char = str(character.get("char", ""))
        center_x = float(character["x"]) + float(character["width"]) / 2
        center_y = float(character["y"]) + float(character["height"]) / 2
        if column["x0"] <= center_x <= column["x1"] and row_top <= center_y <= row_bottom:
            pieces.append((int(character.get("lineIndex", 0)), float(character["x"]), char))
    pieces.sort()
    result = ""
    previous_line = None
    for line_index, _x, char in pieces:
        if previous_line is not None and line_index != previous_line and result and not result.endswith(" "):
            result += " "
        result += char
        previous_line = line_index
    return " ".join(result.split())


def detect_header(
    page_geometry: dict,
    columns: list[dict],
    rows: list[dict],
    *,
    ruled: bool = False,
) -> dict | None:
    if len(rows) < 2 or len(columns) < 2:
        return None
    row_texts = [[_cell_text(page_geometry, column, [row]) for column in columns] for row in rows[:3]]

    def header_score(values: list[str]) -> float:
        nonempty = [value for value in values if value]
        if not nonempty:
            return 0.0
        alpha = sum(any(character.isalpha() for character in value) for value in nonempty)
        return alpha / len(nonempty)

    def numeric_score(values: list[str]) -> float:
        nonempty = [value for value in values if value]
        if not nonempty:
            return 0.0
        return sum(bool(_NUMBER.match(value)) for value in nonempty) / len(nonempty)

    row_count = 0
    if header_score(row_texts[0]) >= 0.5 and numeric_score(row_texts[1]) >= 0.25:
        row_count = 1
    elif len(row_texts) >= 3 and header_score(row_texts[0]) >= 0.5 and header_score(row_texts[1]) >= 0.5 and numeric_score(row_texts[2]) >= 0.25:
        row_count = 2
    elif (
        ruled
        and header_score(row_texts[0]) >= 0.9
        and sum(bool(value) for value in row_texts[0]) >= max(2, len(columns) - 1)
    ):
        # Generic ruled forms may contain no numeric body columns at all. A first
        # band that labels essentially every column is still strong header evidence.
        row_count = 1
    if row_count == 0:
        return None
    for row in rows[:row_count]:
        row["kind"] = "header"
    return {
        "rowCount": row_count,
        "labels": [_cell_text(page_geometry, column, rows[:row_count]) for column in columns],
    }
