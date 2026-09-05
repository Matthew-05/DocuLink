"""Render detected fs-values as colored rectangles over a diagnostic PDF.

Published values are drawn in their kind's color. Pass --show-rejected to also
draw the candidates that were recognized and then ruled out, each labelled with
the reason it lost -- the only way to inspect a suppressed value, since nothing
in the production path carries one.
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

from engines.fs_values.detector import detect_fs_values  # noqa: E402


COLORS = {
    "number": (0.04, 0.45, 0.56),
    "percent": (0.71, 0.33, 0.04),
    "date": (0.08, 0.50, 0.24),
}
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
    model = detect_fs_values(geometry_for(pdf_bytes), diagnostics=diagnostics)
    document = pymupdf.open(stream=pdf_bytes, filetype="pdf")
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
                    _draw(document[page_index], region["bounds"], COLORS[value["kind"]])
        for page_values in model["pages"] if show_rejected else []:
            page_index = page_values["pageIndex"]
            if pages is not None and page_index + 1 not in pages:
                continue
            for candidate in page_values.get("noise", []):
                _draw(
                    document[page_index],
                    candidate["bounds"],
                    REJECTED_COLOR,
                    candidate["reason"],
                )
        output.parent.mkdir(parents=True, exist_ok=True)
        document.save(output)
    finally:
        document.close()
    if report is not None:
        report.parent.mkdir(parents=True, exist_ok=True)
        payload = {"model": model, "diagnostics": diagnostics}
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
