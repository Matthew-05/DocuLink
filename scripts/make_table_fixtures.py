"""Generate the committed synthetic table fixtures and their goldens.

The Apple filing that exposed most of the detector's failures cannot be
committed, so each failure mode it demonstrated is reproduced here as a small
born-digital PDF: two schemas stacked in one band, two tables separated by
prose, an exhibit index with wrapped cells, a ruled grid, and four negatives —
a bullet list, a numbered list, a chart and a two-column prose layout.

The goldens are derived from the layout that was drawn, never from detector
output, so they remain an independent oracle. Re-running this script rewrites
both the PDFs and the goldens; review the diff before committing.
"""
from __future__ import annotations

import json
import os
from pathlib import Path

import pymupdf as fitz


REPO_ROOT = Path(__file__).resolve().parents[1]
FIXTURES = REPO_ROOT / "src" / "python" / "tests" / "fixtures" / "tables" / "synthetic"

PAGE_WIDTH = 612.0
PAGE_HEIGHT = 792.0
FONT = "helv"
BODY_SIZE = 9.0
LINE_PITCH = 14.0


class Sheet:
    """One drawn page, remembering the ink box of everything placed on it."""

    def __init__(self, document: fitz.Document) -> None:
        self.page = document.new_page(width=PAGE_WIDTH, height=PAGE_HEIGHT)

    def text(self, x: float, baseline: float, value: str, *, size: float = BODY_SIZE, right: bool = False):
        width = fitz.get_text_length(value, fontname=FONT, fontsize=size)
        left = x - width if right else x
        self.page.insert_text((left, baseline), value, fontname=FONT, fontsize=size)
        return (
            left / PAGE_WIDTH,
            (baseline - size * 0.74) / PAGE_HEIGHT,
            (left + width) / PAGE_WIDTH,
            (baseline + size * 0.22) / PAGE_HEIGHT,
        )

    def rule(self, x0: float, x1: float, y: float, width: float = 0.6) -> None:
        self.page.draw_line((x0, y), (x1, y), width=width)

    def vertical_rule(self, x: float, y0: float, y1: float, width: float = 0.6) -> None:
        self.page.draw_line((x, y0), (x, y1), width=width)


def _bounds(boxes) -> dict:
    x0 = min(box[0] for box in boxes)
    y0 = min(box[1] for box in boxes)
    x1 = max(box[2] for box in boxes)
    y1 = max(box[3] for box in boxes)
    return {"x": x0, "y": y0, "width": x1 - x0, "height": y1 - y0}


def _table_golden(rows, header_rows: int, bounds: dict, column_edges=None) -> dict:
    """Turn drawn ink into the golden table shape.

    Column boundaries sit in the middle of the corridor between one column's
    rightmost ink and the next column's leftmost; row boundaries sit halfway
    between consecutive baselines. Both are what a correct detector must find.
    """
    column_count = max(max(row["cells"]) for row in rows) + 1
    edges: list[float] = list(column_edges or [])
    if column_edges is None:
        edges = []
    for index in range(column_count - 1):
        right = max(
            (box[2] for row in rows for position, box in row["cells"].items() if position == index),
            default=None,
        )
        left = min(
            (box[0] for row in rows for position, box in row["cells"].items() if position == index + 1),
            default=None,
        )
        if right is None or left is None:
            continue
        edges.append((right + left) / 2)
    if column_edges is not None:
        edges = list(column_edges)
    boundaries = [bounds["x"]] + edges + [bounds["x"] + bounds["width"]]
    columns = [
        {"x0": boundaries[index], "x1": boundaries[index + 1]}
        for index in range(len(boundaries) - 1)
    ]
    centers = [row["center"] for row in rows]
    row_edges = [bounds["y"]]
    for index in range(len(centers) - 1):
        row_edges.append((centers[index] + centers[index + 1]) / 2)
    row_edges.append(bounds["y"] + bounds["height"])
    emitted = []
    for index, row in enumerate(rows):
        emitted.append(
            {
                "y0": row_edges[index],
                "y1": row_edges[index + 1],
                "kind": "header" if index < header_rows else "body",
                "textLines": row["lines"],
                "merged": len(row["lines"]) > 1,
                "mergeConfidence": 0.9 if len(row["lines"]) > 1 else 0.0,
            }
        )
    return {
        "bounds": bounds,
        "columns": columns,
        "rows": emitted,
        "header": {"rowCount": header_rows} if header_rows else None,
    }


class Builder:
    """Collects the rows of one table as they are drawn."""

    def __init__(self, sheet: Sheet) -> None:
        self.sheet = sheet
        self.rows: list[dict] = []
        self.boxes: list[tuple] = []

    def row(self, baseline: float, cells: dict[int, tuple[float, str, bool]], *, size: float = BODY_SIZE):
        placed: dict[int, tuple] = {}
        for index, (x, value, right) in cells.items():
            box = self.sheet.text(x, baseline, value, size=size, right=right)
            placed[index] = box
            self.boxes.append(box)
        line = {
            "y0": min(box[1] for box in placed.values()),
            "y1": max(box[3] for box in placed.values()),
        }
        self.rows.append(
            {
                "cells": placed,
                "center": (line["y0"] + line["y1"]) / 2,
                "lines": [line],
            }
        )
        return self.rows[-1]

    def money(self, baseline: float, label: str, cells: list[tuple[float, str, bool]]):
        """A row of right-aligned amounts, each optionally marked with a currency.

        The marker floats at the left of its cell, well clear of the amount at the
        right — the arrangement every accounting layout uses, and the one that
        tempts a column boundary to fall between a symbol and the number it
        belongs to.
        """
        placed: dict[int, tuple] = {}
        if label:
            box = self.sheet.text(72.0, baseline, label)
            placed[0] = box
            self.boxes.append(box)
        for index, (right_edge, amount, marked) in enumerate(cells, start=1):
            boxes = []
            if marked:
                boxes.append(self.sheet.text(right_edge - 80.0, baseline, "$"))
            boxes.append(self.sheet.text(right_edge, baseline, amount, right=True))
            self.boxes.extend(boxes)
            placed[index] = (
                min(box[0] for box in boxes),
                min(box[1] for box in boxes),
                max(box[2] for box in boxes),
                max(box[3] for box in boxes),
            )
        line = {
            "y0": min(box[1] for box in placed.values()),
            "y1": max(box[3] for box in placed.values()),
        }
        self.rows.append(
            {"cells": placed, "center": (line["y0"] + line["y1"]) / 2, "lines": [line]}
        )
        return self.rows[-1]

    def wrap(self, baseline: float, column: int, x: float, value: str):
        """A continuation line of the previous row's cell."""
        box = self.sheet.text(x, baseline, value)
        self.boxes.append(box)
        row = self.rows[-1]
        row["lines"].append({"y0": box[1], "y1": box[3]})
        row["center"] = (row["lines"][0]["y0"] + row["lines"][-1]["y1"]) / 2

    def golden(self, header_rows: int, pad: float = 0.006, column_edges=None) -> dict:
        bounds = _bounds(self.boxes)
        bounds = {
            "x": max(0.0, bounds["x"] - pad),
            "y": max(0.0, bounds["y"] - pad / 2),
            "width": bounds["width"] + pad * 2,
            "height": bounds["height"] + pad,
        }
        return _table_golden(self.rows, header_rows, bounds, column_edges)


def _prose(sheet: Sheet, baseline: float, lines: list[str]) -> float:
    for line in lines:
        sheet.text(72.0, baseline, line)
        baseline += LINE_PITCH
    return baseline


# --- fixtures -----------------------------------------------------------------


def whitespace_financial(sheet: Sheet) -> list[dict]:
    """A period header, section captions, wrapped totals: the common statement."""
    builder = Builder(sheet)
    builder.row(120.0, {1: (330.0, "2025", True), 2: (450.0, "2024", True), 3: (560.0, "2023", True)})
    builder.row(140.0, {0: (72.0, "Net sales:", False)})
    for offset, (label, a, b, c) in enumerate(
        [
            ("Products", "294,866", "298,085", "298,085"),
            ("Services", "109,158", "96,169", "85,200"),
            ("Total net sales", "404,024", "394,254", "383,285"),
        ]
    ):
        baseline = 158.0 + offset * LINE_PITCH
        builder.row(
            baseline,
            {
                0: (86.0, label, False),
                1: (330.0, a, True),
                2: (450.0, b, True),
                3: (560.0, c, True),
            },
        )
    return [builder.golden(header_rows=1)]


def two_schemas(sheet: Sheet) -> list[dict]:
    """Three value columns above two: one band, two tables."""
    first = Builder(sheet)
    first.row(120.0, {1: (330.0, "2025", True), 2: (450.0, "2024", True), 3: (560.0, "2023", True)})
    for offset, (label, a, b, c) in enumerate(
        [("U.S.", "151,790", "142,196", "138,573"), ("China", "64,377", "66,952", "72,559"),
         ("Other countries", "199,994", "181,887", "172,153")]
    ):
        first.row(
            138.0 + offset * LINE_PITCH,
            {0: (86.0, label, False), 1: (330.0, a, True), 2: (450.0, b, True), 3: (560.0, c, True)},
        )
    second = Builder(sheet)
    second.row(206.0, {1: (450.0, "2025", True), 2: (560.0, "2024", True)})
    for offset, (label, a, b) in enumerate(
        [("U.S.", "40,274", "35,664"), ("China", "3,617", "4,797"), ("Other countries", "5,943", "5,219")]
    ):
        second.row(
            224.0 + offset * LINE_PITCH,
            {0: (86.0, label, False), 1: (450.0, a, True), 2: (560.0, b, True)},
        )
    return [first.golden(header_rows=1), second.golden(header_rows=1)]


def prose_between(sheet: Sheet) -> list[dict]:
    """Two tables with three paragraphs between them."""
    first = Builder(sheet)
    first.row(120.0, {1: (360.0, "2025", True), 2: (500.0, "2024", True)})
    for offset, (label, a, b) in enumerate(
        [("Products", "112,887", "109,633"), ("Services", "82,314", "71,050"), ("Total", "195,201", "180,683")]
    ):
        first.row(
            138.0 + offset * LINE_PITCH,
            {0: (86.0, label, False), 1: (360.0, a, True), 2: (500.0, b, True)},
        )
    _prose(
        sheet,
        212.0,
        [
            "Products gross margin increased during 2025 compared to 2024 due primarily to a different",
            "product mix and a higher proportion of the total sold through the direct channel, partially",
            "offset by the weakness in foreign currencies relative to the U.S. dollar during the period.",
            "The Company believes the trend is likely to continue into the following fiscal year.",
        ],
    )
    second = Builder(sheet)
    second.row(300.0, {1: (360.0, "2025", True), 2: (500.0, "2024", True)})
    for offset, (label, a, b) in enumerate(
        [
            ("Research and development", "34,550", "31,370"),
            ("Selling and administrative", "27,601", "26,097"),
            ("Total operating expenses", "62,151", "57,467"),
        ]
    ):
        second.row(
            318.0 + offset * LINE_PITCH,
            {0: (86.0, label, False), 1: (360.0, a, True), 2: (500.0, b, True)},
        )
    return [first.golden(header_rows=1), second.golden(header_rows=1)]


def wrapped_cells(sheet: Sheet) -> list[dict]:
    """An exhibit index: a wide description column that wraps under itself."""
    builder = Builder(sheet)
    builder.row(
        110.0,
        {0: (72.0, "Exhibit", False), 1: (150.0, "Description", False), 2: (470.0, "Form", False), 3: (540.0, "Date", False)},
    )
    entries = [
        ("4.9", ["Officer's Certificate of the Registrant, including the form of global", "notes representing the 1.375% Notes due 2024."], "8-K", "9/17/15"),
        ("4.10", ["Officer's Certificate of the Registrant, including the form of global", "notes representing the Floating Rate Notes due 2019."], "8-K", "2/23/16"),
        ("4.11", ["Supplement No. 1 to the Officer's Certificate of the Registrant."], "8-K", "3/24/16"),
        ("4.12", ["Officer's Certificate of the Registrant, including the form of global", "notes representing the 2.450% Notes due 2026."], "8-K", "8/4/16"),
    ]
    baseline = 134.0
    for number, description, form, date in entries:
        builder.row(
            baseline,
            {0: (72.0, number, False), 1: (150.0, description[0], False), 2: (470.0, form, False), 3: (540.0, date, False)},
        )
        for extra in description[1:]:
            baseline += 12.0
            builder.wrap(baseline, 1, 158.0, extra)
        baseline += 22.0
    return [builder.golden(header_rows=1)]


def ruled_grid(sheet: Sheet) -> list[dict]:
    """A fully bordered grid, the shape a scanned form produces."""
    builder = Builder(sheet)
    columns = [72.0, 220.0, 360.0, 500.0]
    rows = [
        ("Item", "Quantity", "Unit price", "Amount"),
        ("Widget", "12", "4.50", "54.00"),
        ("Gadget", "3", "19.95", "59.85"),
        ("Sprocket", "40", "1.25", "50.00"),
    ]
    for index, values in enumerate(rows):
        builder.row(
            120.0 + index * 24.0,
            {position: (columns[position] + 6.0, value, False) for position, value in enumerate(values)},
        )
    top, bottom = 104.0, 104.0 + len(rows) * 24.0
    for index in range(len(rows) + 1):
        sheet.rule(72.0, 578.0, top + index * 24.0)
    for x in columns + [578.0]:
        sheet.vertical_rule(x, top, bottom)
    # A ruled grid's columns are the rules themselves, not the whitespace between
    # the words, so the golden says so explicitly.
    return [
        builder.golden(
            header_rows=1,
            column_edges=[x / PAGE_WIDTH for x in columns[1:]],
        )
    ]


def intro_paragraph(sheet: Sheet) -> list[dict]:
    """A table introduced by a sentence that wraps onto a short second line.

    The tail of that sentence sits one line above the header on paragraph
    leading. It is short, so it does not read as running text on its own, and it
    used to be admitted as the table's caption — putting a fragment of prose in
    the first row of the extracted grid.
    """
    _prose(
        sheet,
        118.0,
        [
            "Share repurchase activity during the three months ended September 27, 2025 was as follows (in",
            "thousands, and per-share amounts):",
        ],
    )
    builder = Builder(sheet)
    # Only a few points below the tail, as a filing sets it: close enough that a
    # reach-upward rule admits the tail unless it recognizes what the tail is.
    builder.row(
        148.0,
        {
            # Every row carries first-column ink, so the corridor between the
            # columns is the same on every line and the golden boundary is not a
            # matter of interpretation.
            0: (72.0, "Periods", False),
            # Header labels no wider than the amounts beneath them, so the
            # corridor between two columns is identical on every row and the
            # golden boundary cannot be a matter of interpretation.
            1: (300.0, "Shares", True),
            2: (430.0, "Price", True),
            3: (560.0, "Value", True),
        },
    )
    for offset, (label, a, b, c) in enumerate(
        [
            ("Open market purchases", "33,265", "210.43", "7,000"),
            ("Open market purchases", "28,986", "224.25", "6,500"),
            ("Total", "62,251", "217.34", "13,500"),
        ]
    ):
        builder.row(
            166.0 + offset * LINE_PITCH,
            {
                0: (72.0, label, False),
                1: (300.0, a, True),
                2: (430.0, b, True),
                3: (560.0, c, True),
            },
        )
    return [builder.golden(header_rows=1)]


def currency_columns(sheet: Sheet) -> list[dict]:
    """Floated currency markers in every arrangement a filing uses.

    Markers on the first and last rows only, markers on every row, a nil dash
    that still takes a marker, negatives in parentheses, and amounts of widely
    different lengths. Each marker has to end up inside the column of the amount
    it marks — which is a statement about where the boundary goes, not only about
    what the cell text says.
    """
    builder = Builder(sheet)
    # Amounts right-aligned here, markers 80pt to their left — clear of the
    # longest row label, as an accounting layout sets them. A marker that
    # overlapped the labels could not be separated by any single boundary.
    columns = (330.0, 450.0, 570.0)
    builder.row(
        120.0,
        {
            1: (columns[0], "2025", True),
            2: (columns[1], "2024", True),
            3: (columns[2], "2023", True),
        },
    )
    body = [
        ("Net sales", [("416,161", True), ("391,035", True), ("383,285", True)]),
        ("Cost of sales", [("(220,960)", False), ("(210,352)", False), ("(214,137)", False)]),
        ("Research and development", [("34,550", False), ("31,370", False), ("29,915", False)]),
        ("Impairment", [("—", True), ("—", True), ("1,050", True)]),
        ("Other income", [("269", False), ("(565)", False), ("382", False)]),
        ("Total", [("133,050", True), ("123,216", True), ("114,301", True)]),
    ]
    for offset, (label, amounts) in enumerate(body):
        builder.money(
            142.0 + offset * LINE_PITCH,
            label,
            [(columns[index], amount, marked) for index, (amount, marked) in enumerate(amounts)],
        )
    return [builder.golden(header_rows=1)]


def bullet_list(sheet: Sheet) -> list[dict]:
    """A negative: a bullet list is two aligned columns and is not a table."""
    sheet.text(72.0, 110.0, "First Quarter 2025:")
    items = ["MacBook Pro", "Mac mini", "iMac", "iPad mini", "Apple Watch Series 11"]
    for index, item in enumerate(items):
        baseline = 132.0 + index * 18.0
        sheet.text(90.0, baseline, "•")
        sheet.text(112.0, baseline, item)
    return []


def numbered_list(sheet: Sheet) -> list[dict]:
    """A negative: an ordinal marker column is not a table column."""
    sheet.text(72.0, 110.0, "We consent to the incorporation by reference in the following:")
    items = [
        "Registration Statement (Form S-3 ASR No. 333-263609)",
        "Registration Statement (Form S-8 No. 333-165214)",
        "Registration Statement (Form S-8 No. 333-195509)",
        "Registration Statement (Form S-8 No. 333-217026)",
        "Registration Statement (Form S-8 No. 333-232562)",
    ]
    for index, item in enumerate(items):
        baseline = 134.0 + index * 18.0
        sheet.text(72.0, baseline, f"({index + 1})")
        sheet.text(100.0, baseline, item)
    return []


def chart(sheet: Sheet) -> list[dict]:
    """A negative: gridlines and a plotted series, with axis labels alongside."""
    left, right, top, bottom = 100.0, 540.0, 120.0, 380.0
    sheet.page.draw_rect(fitz.Rect(left, top, right, bottom), color=(0.9, 0.9, 0.9), fill=(0.97, 0.97, 0.97))
    for index in range(6):
        y = top + index * (bottom - top) / 5
        sheet.rule(left, right, y, width=0.4)
        sheet.text(left - 8.0, y + 3.0, f"{(5 - index) * 50}", right=True)
    points = [(left + index * (right - left) / 5, bottom - (index * 37 + 20)) for index in range(6)]
    for index in range(len(points) - 1):
        sheet.page.draw_line(points[index], points[index + 1], width=1.2)
    for index in range(6):
        sheet.text(left + index * (right - left) / 5, bottom + 14.0, f"202{index}")
    return []


def aligned_prose(sheet: Sheet) -> list[dict]:
    """A negative: a headerless two-column prose layout."""
    left = [
        "The Company is subject to various legal proceedings and claims that",
        "have arisen in the ordinary course of business and that have not been",
        "fully resolved. The outcome of litigation is inherently uncertain.",
        "In the opinion of management, there was not at least a reasonable",
    ]
    right = [
        "possibility the Company may have incurred a material loss with",
        "respect to loss contingencies for asserted legal and other claims.",
        "However, the outcome of legal proceedings and claims brought",
        "against the Company is subject to significant uncertainty.",
    ]
    for index in range(len(left)):
        baseline = 120.0 + index * LINE_PITCH
        sheet.text(72.0, baseline, left[index])
        sheet.text(330.0, baseline, right[index])
    return []


FIXTURE_BUILDERS = {
    "whitespace-financial": whitespace_financial,
    "two-schemas": two_schemas,
    "prose-between": prose_between,
    "wrapped-cells": wrapped_cells,
    "intro-paragraph": intro_paragraph,
    "currency-columns": currency_columns,
    "ruled-grid": ruled_grid,
    "negative-bullet-list": bullet_list,
    "negative-numbered-list": numbered_list,
    "negative-chart": chart,
    "negative-aligned-prose": aligned_prose,
}


def _replace(path: Path, payload: bytes) -> bool:
    """Write a fixture through a temporary file and rename it into place.

    Rewriting in place fails whenever something else holds the file open — a PDF
    viewer, an indexer — and leaves a half-written fixture behind if it fails
    part way. A rename either happens or does not. A file that cannot be replaced
    at all is reported and skipped, so one locked fixture does not abandon the
    rest of the set half-rebuilt.
    """
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_bytes(payload)
    try:
        os.replace(temporary, path)
    except OSError as error:
        temporary.unlink(missing_ok=True)
        print(f"[skipped] {path.name}: {error.strerror or error}")
        return False
    return True


def build(output: Path = FIXTURES) -> list[Path]:
    output.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []
    for name, builder in FIXTURE_BUILDERS.items():
        document = fitz.open()
        sheet = Sheet(document)
        tables = builder(sheet)
        pdf_path = output / f"{name}.pdf"
        wrote_pdf = _replace(pdf_path, document.tobytes(deflate=True, garbage=3))
        document.close()
        golden = {
            "pageIndex": 0,
            "tables": [
                {
                    "id": f"page-0-table-{index}",
                    "evidence": "whitespace",
                    "confidence": 0.9,
                    "rulings": {"vertical": [], "horizontal": []},
                    **table,
                }
                for index, table in enumerate(tables)
            ],
        }
        golden_path = output / f"{name}.page-1.json"
        # The golden describes the PDF beside it, so it is only rewritten when
        # that PDF was.
        if wrote_pdf and _replace(golden_path, (json.dumps(golden, indent=2) + "\n").encode("utf-8")):
            written.extend([pdf_path, golden_path])
    return written


if __name__ == "__main__":
    for path in build():
        print(path)
