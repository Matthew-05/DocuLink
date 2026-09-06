"""Render detected values as colored rectangles over a diagnostic PDF.

Published values are drawn in their kind's color and references in their own,
so the two click layers can be told apart at a glance. Pass --show-rejected to
also draw what was recognized and then refused, each labelled with the reason it
lost.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import pymupdf

SCRIPT_ROOT = Path(__file__).resolve().parent
if str(SCRIPT_ROOT) not in sys.path:
    sys.path.insert(0, str(SCRIPT_ROOT))

from table_corpus import geometry_for

from engines.fs.detector import detect_fs_structure  # noqa: E402
from engines.values.detector import detect_values  # noqa: E402
from engines.values.lines import prepare  # noqa: E402


COLORS = {
    "number": (0.04, 0.45, 0.56),
    "percent": (0.71, 0.33, 0.04),
    "date": (0.08, 0.50, 0.24),
}
REFERENCE_COLOR = (0.42, 0.29, 0.62)
REJECTED_COLOR = (0.80, 0.15, 0.15)


def _page_set(value: str | None) -> set[int] | None:
    if not value:
        return None
    pages: set[int] = set()
    for part in value.split(","):
        part = part.strip()
        if "-" in part:
            start, end = part.split("-", 1)
            pages.update(range(int(start), int(end) + 1))
        elif part:
            pages.add(int(part))
    return pages


def _draw(page, bounds: dict, color: tuple[float, float, float], label: str = "") -> None:
    width, height = page.rect.width, page.rect.height
    rect = pymupdf.Rect(
        bounds["x"] * width,
        bounds["y"] * height,
        (bounds["x"] + bounds["width"]) * width,
        (bounds["y"] + bounds["height"]) * height,
    )
    page.draw_rect(rect, color=color, width=0.8, overlay=True)
    if label:
        page.insert_text(
            pymupdf.Point(rect.x0, max(6.0, rect.y0 - 1.5)),
            label,
            fontsize=3.6,
            color=color,
            overlay=True,
        )


def render(
    source: Path,
    output: Path,
    pages: set[int] | None = None,
    report: Path | None = None,
    show_rejected: bool = False,
) -> Path:
    pdf_bytes = source.read_bytes()
    diagnostics: dict = {}
    document = prepare(geometry_for(pdf_bytes))
    structure = detect_fs_structure(document)
    model = detect_values(document, claims=structure.spans, diagnostics=diagnostics)
    document_pdf = pymupdf.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page_values in model["pages"]:
            for value in page_values["values"]:
                regions = value.get("segments") or [{
                    "pageIndex": page_values["pageIndex"],
                    "bounds": value["bounds"],
                }]
                for region in regions:
                    page_index = region["pageIndex"]
                    if pages is not None and page_index + 1 not in pages:
                        continue
                    _draw(document_pdf[page_index], region["bounds"], COLORS[value["kind"]])
        for page_values in model["pages"]:
            page_index = page_values["pageIndex"]
            if pages is not None and page_index + 1 not in pages:
                continue
            for reference in page_values.get("references", []):
                _draw(
                    document_pdf[page_index],
                    reference["bounds"],
                    REFERENCE_COLOR,
                    reference["kind"],
                )
        for page_values in model["pages"] if show_rejected else []:
            page_index = page_values["pageIndex"]
            if pages is not None and page_index + 1 not in pages:
                continue
            for candidate in page_values.get("noise", []):
                _draw(
                    document_pdf[page_index],
                    candidate["bounds"],
                    REJECTED_COLOR,
                    candidate["reason"],
                )
        output.parent.mkdir(parents=True, exist_ok=True)
        document_pdf.save(output)
    finally:
        document_pdf.close()
    if report is not None:
        report.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "values": model,
            "structure": structure.model,
            "diagnostics": diagnostics,
        }
        report.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")
    return output


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--page", help="1-based page, comma list, or range")
    parser.add_argument("--write-report", type=Path)
    parser.add_argument("--show-rejected", action="store_true")
    args = parser.parse_args()
    print(render(args.pdf, args.out, _page_set(args.page), args.write_report, args.show_rejected))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
