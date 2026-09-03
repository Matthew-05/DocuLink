"""Render detected table structure as an overlay on PDF page images."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
PYTHON_ROOT = REPO_ROOT / "src" / "python"
sys.path.insert(0, str(PYTHON_ROOT))

import pymupdf as fitz  # noqa: E402
from PIL import Image, ImageDraw  # noqa: E402

from engines.geometry_engine import extract_text_geometry  # noqa: E402
from engines.ocr_engine import extract_direct_text_geometry  # noqa: E402
from engines.table.detector import detect_tables  # noqa: E402


def _geometry_with_sparse_page_ocr(pdf_bytes: bytes) -> dict:
    geometry = extract_text_geometry(pdf_bytes)
    sparse_pages = [
        page["pageIndex"] + 1
        for page in geometry["pages"]
        if sum(bool(str(char.get("char", "")).strip()) for char in page["characters"]) < 20
    ]
    if sparse_pages:
        ocr_pages, _ = extract_direct_text_geometry(pdf_bytes, sparse_pages, dpi=300, psm=6)
        replacements = {page_number - 1: page for page_number, page in ocr_pages.items()}
        geometry["pages"] = [
            replacements.get(page["pageIndex"], page) for page in geometry["pages"]
        ]
    return geometry


def _draw_table(image: Image.Image, table: dict) -> None:
    width, height = image.size
    bounds = table["bounds"]
    left = float(bounds["x"]) * width
    top = float(bounds["y"]) * height
    right = (float(bounds["x"]) + float(bounds["width"])) * width
    bottom = (float(bounds["y"]) + float(bounds["height"])) * height

    header = table.get("header")
    rows = table["rows"]
    if header is not None:
        overlay = Image.new("RGBA", image.size, (0, 0, 0, 0))
        overlay_draw = ImageDraw.Draw(overlay)
        for row in rows[: int(header["rowCount"])]:
            overlay_draw.rectangle(
                (left, float(row["y0"]) * height, right, float(row["y1"]) * height),
                fill=(255, 255, 0, 80),
            )
        image.alpha_composite(overlay)
        overlay.close()

    draw = ImageDraw.Draw(image)
    draw.rectangle((left, top, right, bottom), outline=(0, 102, 255, 255), width=3)
    for column in table["columns"][:-1]:
        x = float(column["x1"]) * width
        draw.line((x, top, x, bottom), fill=(255, 0, 0, 255), width=3)
    for row in rows:
        y = float(row["y1"]) * height
        draw.line((left, y, right, y), fill=(0, 170, 0, 255), width=3)


def render_overlay(pdf_path: Path, out_pdf: Path, *, dpi: int = 110) -> Path:
    """Render detected tables over source pages and write table-bearing pages to PDF."""
    pdf_path = Path(pdf_path)
    out_pdf = Path(out_pdf)
    pdf_bytes = pdf_path.read_bytes()
    geometry = _geometry_with_sparse_page_ocr(pdf_bytes)
    model = detect_tables(pdf_bytes, geometry)
    tables_by_page = {
        int(page["pageIndex"]): page["tables"]
        for page in model["pages"]
        if page["tables"]
    }

    rendered_pages: list[Image.Image] = []
    document = fitz.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page_index in range(document.page_count):
            tables = tables_by_page.get(page_index)
            if not tables:
                continue
            pixmap = document.load_page(page_index).get_pixmap(dpi=dpi, alpha=False)
            image = Image.frombytes("RGB", (pixmap.width, pixmap.height), pixmap.samples).convert(
                "RGBA"
            )
            for table in tables:
                _draw_table(image, table)
            rendered_page = image.convert("RGB")
            image.close()
            rendered_pages.append(rendered_page)
    finally:
        document.close()

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


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("pdf", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--dpi", type=int, default=110)
    args = parser.parse_args()

    output = render_overlay(args.pdf, args.out, dpi=args.dpi)
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
