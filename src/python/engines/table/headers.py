"""Detect a leading header band and merge its label text by column."""
from __future__ import annotations

import re


_NUMBER = re.compile(r"^[\s($+\-]*[\d,.%]+[)\s]*$")
# Period labels carry no alphabetic characters at all, so alpha-density scoring
# rejects them. They are still the strongest header evidence a financial
# statement offers.
_PERIOD = re.compile(
    r"^(?:(?:19|20)\d{2}|FY\s*\d{2,4}|Q[1-4](?:\s*(?:19|20)\d{2})?"
    r"|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?"
    r"(?:\s+\d{1,2})?(?:,?\s*(?:19|20)\d{2})?)$",
    re.IGNORECASE,
)


def _cell_text(page_geometry: dict, column: dict, rows: list[dict], bounds: dict | None = None) -> str:
    pieces: list[tuple[int, float, str]] = []
    row_top = min(row["y0"] for row in rows)
    row_bottom = max(row["y1"] for row in rows)
    if bounds is not None:
        # Row bands run to the table edge, so an unclamped scan can pull a caption
        # sitting directly above the table into the first header label.
        row_top = max(row_top, bounds["y"])
        row_bottom = min(row_bottom, bounds["y"] + bounds["height"])
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


def _is_caption_row(values: list[str], filled_columns: list[int]) -> bool:
    """A section label such as "Deferred tax assets:" carrying no values."""
    return bool(values) and bool(values[0]) and not any(
        values[index] for index in filled_columns if index < len(values)
    )


def _is_offset_period_band(values: list[str], below: list[str]) -> bool:
    """A period band that starts partway across the table, leaving cell 0 empty.

    `2025 2024 2023` over an unlabelled first column is the standard financial
    statement shape. It is only accepted as a header when the band below it is
    numeric in the same columns, so a value-only body row can never be promoted.
    """
    if not values or values[0]:
        return False
    filled = [(index, value) for index, value in enumerate(values) if value]
    if len(filled) < 2:
        return False
    # "2025 | Change | 2024 | Change | 2023" is one band: a comparison column
    # labelled in words sits between the periods.
    if not all(
        _PERIOD.match(value) or any(character.isalpha() for character in value)
        for _index, value in filled
    ):
        return False
    if not any(_PERIOD.match(value) for _index, value in filled):
        return False
    numeric_below = [
        index
        for index, _value in filled
        if index < len(below) and below[index] and _NUMBER.match(below[index])
    ]
    return len(numeric_below) >= max(2, len(filled) - 1)


def detect_header(
    page_geometry: dict,
    columns: list[dict],
    rows: list[dict],
    *,
    ruled: bool = False,
    bounds: dict | None = None,
) -> dict | None:
    if len(rows) < 2 or len(columns) < 2:
        return None
    row_texts = [
        [_cell_text(page_geometry, column, [row], bounds) for column in columns]
        for row in rows[:3]
    ]

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

    def labels_a_real_column(index: int) -> bool:
        """The first column must actually carry row labels below the header."""
        body = [
            _cell_text(page_geometry, columns[0], [row], bounds)
            for row in rows[index : index + 5]
        ]
        return bool(body) and sum(bool(value) for value in body) * 2 > len(body)

    row_count = 0
    # The offset-period branches come first: a period band is itself numeric, so a
    # spanning caption above one would otherwise satisfy the generic alpha-then-
    # numeric rule and claim the whole header, leaving the periods as body data.
    if (
        len(row_texts) >= 3
        and header_score(row_texts[0]) >= 0.5
        and _is_offset_period_band(row_texts[1], row_texts[2])
        and labels_a_real_column(2)
    ):
        # A spanning caption such as "Years ended December 31," above the periods.
        row_count = 2
    elif any(
        _is_offset_period_band(row_texts[0], row_texts[index]) for index in range(1, len(row_texts))
    ) and labels_a_real_column(1):
        # A statement often puts a section caption ("Deferred tax assets:") between
        # the period band and the first numbers. The periods are still the header.
        row_count = 1
    elif header_score(row_texts[0]) >= 0.5 and numeric_score(row_texts[1]) >= 0.25:
        row_count = 1
    elif len(row_texts) >= 3 and header_score(row_texts[0]) >= 0.5 and header_score(row_texts[1]) >= 0.5 and numeric_score(row_texts[2]) >= 0.25:
        row_count = 2
    elif (
        header_score(row_texts[0]) >= 0.9
        and sum(bool(value) for value in row_texts[0]) >= max(2, len(columns) - 1)
        and (ruled or len(rows) >= 3)
    ):
        # Forms and reference tables (an exhibit index, a class-of-stock table) may
        # contain no numeric column at all, so the numeric-body test never fires.
        # A first band that labels essentially every column is still strong header
        # evidence; requiring a body of at least two rows keeps a two-line block
        # from declaring itself a header.
        row_count = 1
    if row_count == 0:
        return None
    for row in rows[:row_count]:
        row["kind"] = "header"
    return {
        "rowCount": row_count,
        "labels": [_cell_text(page_geometry, column, rows[:row_count], bounds) for column in columns],
    }
