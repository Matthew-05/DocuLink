"""Render detected spans as category-colored rectangles over a diagnostic PDF.

Values, references, structure, and noise each use one color, regardless of
their specific type, so the categories can be told apart at a glance. Pass
--show-rejected to also draw noise, labelled with the reason it was refused.
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

from engines.financial.detector import detect_financial_structure  # noqa: E402
from engines.values.detector import detect_values  # noqa: E402
from engines.values.lines import prepare  # noqa: E402


CATEGORY_COLORS = {
    "value": (0.04, 0.45, 0.56),
    "reference": (0.42, 0.29, 0.62),
    "structure": (0.26, 0.22, 0.79),
    "noise": (0.80, 0.15, 0.15),
}


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
    structure = detect_financial_structure(document)
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
                    _draw(document_pdf[page_index], region["bounds"], CATEGORY_COLORS["value"])
        for page_values in model["pages"]:
            page_index = page_values["pageIndex"]
            if pages is not None and page_index + 1 not in pages:
                continue
            for reference in page_values.get("references", []):
                _draw(
                    document_pdf[page_index],
                    reference["bounds"],
                    CATEGORY_COLORS["reference"],
                    reference["kind"],
                )
            for span in page_values.get("structure", []):
                _draw(
                    document_pdf[page_index],
                    span["bounds"],
                    CATEGORY_COLORS["structure"],
                    span["kind"],
                )
        for page_values in model["pages"] if show_rejected else []:
            page_index = page_values["pageIndex"]
            if pages is not None and page_index + 1 not in pages:
                continue
            for candidate in page_values.get("noise", []):
                _draw(
                    document_pdf[page_index],
                    candidate["bounds"],
                    CATEGORY_COLORS["noise"],
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
