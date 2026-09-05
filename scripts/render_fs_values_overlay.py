"""Render detected fs-values as colored rectangles over a diagnostic PDF."""
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

from engines.fs_values.detector import detect_fs_values  # noqa: E402


COLORS = {
    "number": (0.04, 0.45, 0.56),
    "percent": (0.71, 0.33, 0.04),
    "date": (0.08, 0.50, 0.24),
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


def render(source: Path, output: Path, pages: set[int] | None = None, report: Path | None = None) -> Path:
    pdf_bytes = source.read_bytes()
    model = detect_fs_values(geometry_for(pdf_bytes))
    document = pymupdf.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page_values in model["pages"]:
            page_number = page_values["pageIndex"] + 1
            if pages is not None and page_number not in pages:
                continue
            page = document[page_values["pageIndex"]]
            width, height = page.rect.width, page.rect.height
            for value in page_values["values"]:
                bounds = value["bounds"]
                rect = pymupdf.Rect(
                    bounds["x"] * width,
                    bounds["y"] * height,
                    (bounds["x"] + bounds["width"]) * width,
                    (bounds["y"] + bounds["height"]) * height,
                )
                page.draw_rect(rect, color=COLORS[value["kind"]], width=0.8, overlay=True)
        output.parent.mkdir(parents=True, exist_ok=True)
        document.save(output)
    finally:
        document.close()
    if report is not None:
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(json.dumps(model, indent=2, ensure_ascii=False), encoding="utf-8")
    return output


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--page", help="1-based page, comma list, or range")
    parser.add_argument("--write-report", type=Path)
    args = parser.parse_args()
    print(render(args.pdf, args.out, _page_set(args.page), args.write_report))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
