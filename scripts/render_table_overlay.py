"""Render detected table structure as an overlay on PDF page images.

Accepted tables are drawn in blue with their columns and rows; rejected
candidates can be drawn alongside them in red, labelled with the reason they
lost. Every box carries the source page number, table id, evidence type,
confidence and header row count, so a rendered sheet can be read without
cross-referencing the JSON.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))

import pymupdf as fitz  # noqa: E402
from PIL import Image, ImageDraw, ImageFont  # noqa: E402

from table_corpus import DEFAULT_DETECTOR, DETECTORS, geometry_for  # noqa: E402

from engines.table.detector import _detect_page_current  # noqa: E402
from engines.table.redesign import detect_page  # noqa: E402


ACCEPTED = (0, 102, 255, 255)
REJECTED = (220, 40, 40, 255)
COLUMN = (255, 0, 0, 255)
ROW = (0, 170, 0, 255)
# Yellow marks whichever band carries the period the data is dated to; blue marks
# the band that names what the data is. A statement dates its columns, a maturity
# schedule dates its rows, and reading the two apart is most of understanding a
# table — so the overlay says which is which rather than shading both alike.
PERIOD_FILL = (255, 255, 0, 80)
LABEL_FILL = (70, 150, 255, 60)
MERGED_ROW = (255, 140, 0, 255)


def _font() -> ImageFont.ImageFont:
    try:
        return ImageFont.truetype("DejaVuSans.ttf", 13)
    except Exception:
        return ImageFont.load_default()


def _label(draw: ImageDraw.ImageDraw, x: float, y: float, text: str, color) -> None:
    font = _font()
    box = draw.textbbox((x, y), text, font=font)
    draw.rectangle((box[0] - 2, box[1] - 1, box[2] + 2, box[3] + 1), fill=(255, 255, 255, 235))
    draw.text((x, y), text, fill=color, font=font)


def _rect(bounds: dict, size: tuple[int, int]) -> tuple[float, float, float, float]:
    width, height = size
    left = float(bounds["x"]) * width
    top = float(bounds["y"]) * height
    return left, top, (float(bounds["x"]) + float(bounds["width"])) * width, (
        float(bounds["y"]) + float(bounds["height"])
    ) * height


def _draw_table(image: Image.Image, table: dict, page_number: int) -> None:
    width, height = image.size
    left, top, right, bottom = _rect(table["bounds"], image.size)
    header = table.get("header")
    rows = table["rows"]
    # Without period analysis there is nothing to tell the two axes apart, so the
    # header keeps the single shade it always had.
    period_known = table.get("period") is not None
    axis = (table.get("period") or {}).get("axis", "none")
    header_rows = int(header["rowCount"]) if header else 0
    overlay = Image.new("RGBA", image.size, (0, 0, 0, 0))
    overlay_draw = ImageDraw.Draw(overlay)
    for row in rows[:header_rows]:
        overlay_draw.rectangle(
            (left, float(row["y0"]) * height, right, float(row["y1"]) * height),
            fill=PERIOD_FILL
            if not period_known or axis in ("columns", "both")
            else LABEL_FILL,
        )
    body = rows[header_rows:]
    if period_known and body and len(table["columns"]) >= 2:
        # The first column holds the row labels. It is the period axis when the
        # rows are dated — a maturity schedule, or the opening and closing
        # balances of an activity table — and otherwise names the rows.
        label_column = table["columns"][0]
        overlay_draw.rectangle(
            (
                float(label_column["x0"]) * width,
                float(body[0]["y0"]) * height,
                float(label_column["x1"]) * width,
                float(body[-1]["y1"]) * height,
            ),
            fill=PERIOD_FILL if axis in ("rows", "both") else LABEL_FILL,
        )
    image.alpha_composite(overlay)
    overlay.close()

    draw = ImageDraw.Draw(image)
    draw.rectangle((left, top, right, bottom), outline=ACCEPTED, width=3)
    for column in table["columns"][:-1]:
        x = float(column["x1"]) * width
        draw.line((x, top, x, bottom), fill=COLUMN, width=3)
    for row in rows:
        y = float(row["y1"]) * height
        # A merged row spans several source lines; drawing it differently makes a
        # wrapped description cell obvious at a glance.
        draw.line(
            (left, y, right, y),
            fill=MERGED_ROW if row.get("merged") else ROW,
            width=3,
        )
    period = table.get("period") or {}
    # The period is reported rather than drawn: its label was excluded from the
    # grid on purpose, so the overlay is the only place it can be checked.
    dated = period.get("table") or " ".join(
        value
        for value in list(period.get("columns", [])) + list(period.get("rows", []))
        if value
    )
    caption = (
        f"p{page_number} {table['id']} {table['evidence']} "
        f"conf={float(table['confidence']):.2f} "
        f"hdr={int(header['rowCount']) if header else 0}"
    )
    if period.get("axis") and period["axis"] != "none":
        caption += f" · periods by {period['axis']}"
    if period.get("qualifier"):
        caption += f" · {period['qualifier']}"
    if dated:
        caption += f" · {dated[:52]}"
    _label(draw, left + 4, max(2.0, top - 18), caption, ACCEPTED)


def _draw_rejected(image: Image.Image, candidate: dict, page_number: int) -> None:
    draw = ImageDraw.Draw(image)
    left, top, right, bottom = _rect(candidate["bounds"], image.size)
    for offset in range(int(left), int(right), 16):
        draw.line((offset, top, min(offset + 8, right), top), fill=REJECTED, width=3)
        draw.line((offset, bottom, min(offset + 8, right), bottom), fill=REJECTED, width=3)
    for offset in range(int(top), int(bottom), 16):
        draw.line((left, offset, left, min(offset + 8, bottom)), fill=REJECTED, width=3)
        draw.line((right, offset, right, min(offset + 8, bottom)), fill=REJECTED, width=3)
    _label(
        draw,
        left + 4,
        min(image.size[1] - 16.0, bottom + 4),
        f"p{page_number} rejected: {candidate['reason']} conf={float(candidate['confidence']):.2f}",
        REJECTED,
    )


def analyse(
    pdf_bytes: bytes,
    *,
    detector: str = DEFAULT_DETECTOR,
    pages: set[int] | None = None,
    want_rejected: bool = False,
    periods: bool | None = None,
):
    """Detect page by page, keeping the rejected candidates when asked."""
    geometry = geometry_for(pdf_bytes)
    by_page = {int(page["pageIndex"]): page for page in geometry["pages"]}
    document = fitz.open(stream=pdf_bytes, filetype="pdf")
    results: dict[int, dict] = {}
    try:
        for page_index in range(document.page_count):
            if pages is not None and page_index + 1 not in pages:
                continue
            page_geometry = by_page.get(page_index, {"pageIndex": page_index, "characters": []})
            page = document.load_page(page_index)
            rejected: list[dict] = [] if want_rejected else None
            if detector == "redesign":
                tables = detect_page(
                    page_geometry,
                    page,
                    page_index=page_index,
                    rejected=rejected,
                    periods=periods,
                )
            else:
                tables = _detect_page_current(page_geometry, page, page_index)
            results[page_index] = {"tables": tables, "rejected": rejected or []}
    finally:
        document.close()
    return results


def render_overlay(
    pdf_path: Path,
    out_pdf: Path,
    *,
    dpi: int = 110,
    detector: str = DEFAULT_DETECTOR,
    pages: set[int] | None = None,
    show_rejected: bool = False,
    report: Path | None = None,
    periods: bool | None = None,
) -> Path:
    pdf_path = Path(pdf_path)
    out_pdf = Path(out_pdf)
    pdf_bytes = pdf_path.read_bytes()
    results = analyse(
        pdf_bytes,
        detector=detector,
        pages=pages,
        want_rejected=show_rejected,
        periods=periods,
    )

    rendered_pages: list[Image.Image] = []
    document = fitz.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page_index in sorted(results):
            found = results[page_index]
            if not found["tables"] and not (show_rejected and found["rejected"]):
                continue
            pixmap = document.load_page(page_index).get_pixmap(dpi=dpi, alpha=False)
            image = Image.frombytes(
                "RGB", (pixmap.width, pixmap.height), pixmap.samples
            ).convert("RGBA")
            if show_rejected:
                for candidate in found["rejected"]:
                    _draw_rejected(image, candidate, page_index + 1)
            for table in found["tables"]:
                _draw_table(image, table, page_index + 1)
            rendered = image.convert("RGB")
            image.close()
            rendered_pages.append(rendered)
    finally:
        document.close()

    if report is not None:
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(
            json.dumps(
                {
                    "document": pdf_path.name,
                    "detector": detector,
                    "pages": [
                        {
                            "page": page_index + 1,
                            "tables": [
                                {
                                    "id": table["id"],
                                    "evidence": table["evidence"],
                                    "confidence": table["confidence"],
                                    "headerRows": int(table["header"]["rowCount"])
                                    if table.get("header")
                                    else 0,
                                    "columns": len(table["columns"]),
                                    "rows": len(table["rows"]),
                                    "bounds": table["bounds"],
                                }
                                for table in results[page_index]["tables"]
                            ],
                            "rejected": results[page_index]["rejected"],
                        }
                        for page_index in sorted(results)
                        if results[page_index]["tables"] or results[page_index]["rejected"]
                    ],
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )

    if not rendered_pages:
        raise ValueError("No detected tables to render")

    out_pdf.parent.mkdir(parents=True, exist_ok=True)
    try:
        rendered_pages[0].save(
            out_pdf,
            "PDF",
            resolution=dpi,
            save_all=True,
            append_images=rendered_pages[1:],
        )
    finally:
        for image in rendered_pages:
            image.close()
    return out_pdf


def _page_set(values: list[str] | None) -> set[int] | None:
    if not values:
        return None
    pages: set[int] = set()
    for value in values:
        for part in str(value).split(","):
            part = part.strip()
            if not part:
                continue
            if "-" in part:
                start, end = part.split("-", 1)
                pages.update(range(int(start), int(end) + 1))
            else:
                pages.add(int(part))
    return pages


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--dpi", type=int, default=110)
    parser.add_argument("--detector", choices=DETECTORS, default=DEFAULT_DETECTOR)
    parser.add_argument("--page", action="append", help="1-based page, list or range")
    parser.add_argument("--show-rejected", action="store_true")
    parser.add_argument(
        "--periods",
        action="store_true",
        help="run the alpha period analysis and shade the period axis apart from the label axis",
    )
    parser.add_argument("--write-report", type=Path)
    args = parser.parse_args()

    output = render_overlay(
        args.pdf,
        args.out,
        dpi=args.dpi,
        detector=args.detector,
        pages=_page_set(args.page),
        show_rejected=args.show_rejected,
        report=args.write_report,
        periods=args.periods or None,
    )
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
