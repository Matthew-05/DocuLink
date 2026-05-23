"""Extract per-character bounding boxes from PDF text layers using PyMuPDF."""
from __future__ import annotations

import base64
import gzip
import json
from typing import Callable

import fitz


def _extract_page_characters_from_rawdict(
    raw: dict,
    page_w: float,
    page_h: float,
) -> list[dict]:
    """Convert a PyMuPDF rawdict page dict to text-geometry-v1 character boxes."""
    if page_w <= 0 or page_h <= 0:
        return []

    characters: list[dict] = []

    for block in raw.get("blocks", []):
        if block.get("type") != 0:
            continue
        for line in block.get("lines", []):
            for span in line.get("spans", []):
                for ch in span.get("chars", []):
                    bbox = ch.get("bbox")
                    if not bbox or len(bbox) < 4:
                        continue

                    x0, y0, x1, y1 = bbox
                    char = ch.get("c", "")
                    if not char:
                        continue

                    width = (x1 - x0) / page_w
                    height = (y1 - y0) / page_h
                    if width <= 0 or height <= 0:
                        continue

                    characters.append(
                        {
                            "char": char,
                            "x": x0 / page_w,
                            "y": y0 / page_h,
                            "width": width,
                            "height": height,
                        }
                    )

    return characters


def _extract_page_characters(page: fitz.Page) -> list[dict]:
    raw = page.get_text("rawdict", sort=True)
    page_rect = page.rect
    return _extract_page_characters_from_rawdict(
        raw,
        page_rect.width,
        page_rect.height,
    )


def extract_text_geometry(
    pdf_bytes: bytes,
    language: str = "eng",
    progress_callback: Callable[[str], None] | None = None,
) -> dict:
    """
    Extract per-character boxes from each page's PDF text layer.

    Returns a text-geometry-v1 dict with normalized top-left coordinates.
    Word spacing comes from literal space characters in the text layer
    (native PDFs for Enhance, ocrmypdf-embedded layer for OCR).
    """
    del language  # retained for worker API compatibility; rawdict is language-agnostic

    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    pages: list[dict] = []

    try:
        page_count = doc.page_count
        for page_index in range(page_count):
            if progress_callback:
                progress_callback(
                    f"Extracting geometry page {page_index + 1} of {page_count}…"
                )

            page = doc.load_page(page_index)
            characters = _extract_page_characters(page)
            pages.append({"pageIndex": page_index, "characters": characters})
    finally:
        doc.close()

    return {
        "version": 1,
        "coordinateSpace": "normalized",
        "pages": pages,
    }


def geometry_to_base64(geometry: dict) -> str:
    """Gzip-compress and base64-encode a text-geometry-v1 dict."""
    json_bytes = json.dumps(geometry, separators=(",", ":")).encode("utf-8")
    compressed = gzip.compress(json_bytes)
    return base64.b64encode(compressed).decode("ascii")
