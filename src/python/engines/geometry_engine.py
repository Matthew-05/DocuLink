"""Extract per-character bounding boxes from PDF text layers using PyMuPDF."""
from __future__ import annotations

import base64
from difflib import SequenceMatcher
import gzip
import json
from typing import Callable

import pymupdf as fitz

# rawdict normally materializes the fully decoded bytes of every image block.
# We discard image blocks (type != 0), so on an OCR'd scan — where each page is
# one full-page raster — that decode is pure waste. Clearing TEXT_PRESERVE_IMAGES
# measured ~210x faster over an 80-page scan (46.2s -> 0.22s) with byte-identical
# character output. PyMuPDF sorts in unrotated page coordinates, so sorting is
# used only for intrinsically upright pages. Rotated pages retain PDF content-
# stream order, which OCRmyPDF writes in recognized reading order.
_RAWDICT_FLAGS = fitz.TEXTFLAGS_RAWDICT & ~fitz.TEXT_PRESERVE_IMAGES

_DUPLICATE_LINE_OVERLAP = 0.8
_PAINTED_CHARACTER_FLAGS = (1 << 3) | (1 << 4)


def _line_text(characters: list[dict]) -> str:
    """Return comparison text without depending on PDF whitespace quirks."""
    return " ".join(
        "".join(str(character.get("char", "")) for character in characters).split()
    )


def _line_bounds(
    characters: list[dict],
) -> tuple[float, float, float, float] | None:
    visible = [
        character
        for character in characters
        if str(character.get("char", "")).strip()
    ]
    if not visible:
        return None
    return (
        min(float(character["x"]) for character in visible),
        min(float(character["y"]) for character in visible),
        max(
            float(character["x"]) + float(character["width"])
            for character in visible
        ),
        max(
            float(character["y"]) + float(character["height"])
            for character in visible
        ),
    )


def _bounds_are_coincident(
    first: tuple[float, float, float, float],
    second: tuple[float, float, float, float],
) -> bool:
    intersection_width = max(
        0.0, min(first[2], second[2]) - max(first[0], second[0])
    )
    intersection_height = max(
        0.0, min(first[3], second[3]) - max(first[1], second[1])
    )
    minimum_width = min(first[2] - first[0], second[2] - second[0])
    minimum_height = min(first[3] - first[1], second[3] - second[1])
    return (
        minimum_width > 0
        and minimum_height > 0
        and intersection_width / minimum_width >= _DUPLICATE_LINE_OVERLAP
        and intersection_height / minimum_height >= _DUPLICATE_LINE_OVERLAP
    )


def _deduplicate_coincident_lines(lines: list[list[dict]]) -> list[dict]:
    """Collapse identical text lines drawn more than once at the same position.

    Some PDFs retain an old invisible OCR layer when a replacement layer is
    added. PyMuPDF exposes both copies as separate lines, so consumers otherwise
    extract the same value twice. Lines are compared as units to avoid deleting
    legitimate repeated characters within one line.
    """
    retained: list[tuple[str, tuple[float, float, float, float] | None, list[dict]]] = []
    bounds_by_text: dict[str, list[tuple[float, float, float, float]]] = {}
    for line in lines:
        text = _line_text(line)
        bounds = _line_bounds(line)
        if text and bounds is not None and any(
            _bounds_are_coincident(bounds, retained_bounds)
            for retained_bounds in bounds_by_text.get(text, [])
        ):
            continue
        retained.append((text, bounds, line))
        if text and bounds is not None:
            bounds_by_text.setdefault(text, []).append(bounds)

    characters: list[dict] = []
    for line_index, (_, _, line) in enumerate(retained):
        for character in line:
            character["lineIndex"] = line_index
            characters.append(character)
    return characters


def _span_is_invisible(span: dict) -> bool:
    character_flags = int(span.get("char_flags", _PAINTED_CHARACTER_FLAGS))
    return int(span.get("alpha", 255)) == 0 or not (
        character_flags & _PAINTED_CHARACTER_FLAGS
    )


def _span_uses_unstrippable_hidden_text(span: dict) -> bool:
    """Whether OCRmyPDF redo mode cannot reliably remove this hidden span.

    Redo strips PDF text rendering mode 3. Text hidden with zero opacity while
    still marked as painted, or with a clipping-only rendering mode, survives
    that operation and would be joined by a second OCR layer.
    """
    character_flags = int(span.get("char_flags", _PAINTED_CHARACTER_FLAGS))
    painted = bool(character_flags & _PAINTED_CHARACTER_FLAGS)
    zero_opacity_painted = int(span.get("alpha", 255)) == 0 and painted
    clipping_only = not painted and character_flags != 0
    return zero_opacity_painted or clipping_only


def find_unstrippable_hidden_text_pages(pdf_bytes: bytes) -> list[int]:
    """Return one-based pages that must be rasterized instead of redo-OCR'd."""
    document = fitz.open(stream=pdf_bytes, filetype="pdf")
    pages: list[int] = []
    try:
        for page_index, page in enumerate(document):
            raw = page.get_text("rawdict", flags=_RAWDICT_FLAGS, sort=False)
            if any(
                _span_uses_unstrippable_hidden_text(span)
                for block in raw.get("blocks", [])
                if block.get("type") == 0
                for line in block.get("lines", [])
                for span in line.get("spans", [])
            ):
                pages.append(page_index + 1)
    finally:
        document.close()
    return pages


def _raw_line_signature(
    line: dict,
) -> tuple[
    str,
    tuple[float, float, float, float],
    bool,
    frozenset[tuple[str, int, int]],
] | None:
    characters = [
        character
        for span in line.get("spans", [])
        for character in span.get("chars", [])
        if str(character.get("c", "")).strip()
    ]
    if not characters:
        return None
    text = "".join(
        str(character.get("c", ""))
        for span in line.get("spans", [])
        for character in span.get("chars", [])
    )
    normalized_text = "".join(text.casefold().split())
    if not normalized_text:
        return None
    bounds = (
        min(float(character["bbox"][0]) for character in characters),
        min(float(character["bbox"][1]) for character in characters),
        max(float(character["bbox"][2]) for character in characters),
        max(float(character["bbox"][3]) for character in characters),
    )
    invisible = any(
        _span_is_invisible(span) for span in line.get("spans", [])
    )
    rendering = frozenset(
        (
            str(span.get("font", "")),
            int(span.get("alpha", 255)),
            int(span.get("char_flags", _PAINTED_CHARACTER_FLAGS)),
        )
        for span in line.get("spans", [])
    )
    return normalized_text, bounds, invisible, rendering


def _layer_line_bounds_overlap(
    first: tuple[float, float, float, float],
    second: tuple[float, float, float, float],
) -> bool:
    intersection_width = max(
        0.0, min(first[2], second[2]) - max(first[0], second[0])
    )
    intersection_height = max(
        0.0, min(first[3], second[3]) - max(first[1], second[1])
    )
    minimum_width = min(first[2] - first[0], second[2] - second[0])
    minimum_height = min(first[3] - first[1], second[3] - second[1])
    return (
        minimum_width > 0
        and minimum_height > 0
        and intersection_width / minimum_width >= 0.5
        and intersection_height / minimum_height >= 0.5
    )


def _layer_line_texts_match(first: str, second: str) -> bool:
    if first == second:
        return True
    if min(len(first), len(second)) >= 3 and (
        first in second or second in first
    ):
        return True
    return (
        min(len(first), len(second)) >= 4
        and SequenceMatcher(None, first, second).ratio() >= 0.75
    )


def find_coincident_text_layer_pages(pdf_bytes: bytes) -> list[int]:
    """Find pages containing overlapping visible or hidden text-layer copies."""
    document = fitz.open(stream=pdf_bytes, filetype="pdf")
    pages: list[int] = []
    try:
        for page_index, page in enumerate(document):
            raw = page.get_text("rawdict", flags=_RAWDICT_FLAGS, sort=False)
            lines = [
                signature
                for block in raw.get("blocks", [])
                if block.get("type") == 0
                for line in block.get("lines", [])
                if (signature := _raw_line_signature(line)) is not None
            ]
            duplicate_found = False
            for index, (text, bounds, invisible, rendering) in enumerate(lines):
                for (
                    prior_text,
                    prior_bounds,
                    prior_invisible,
                    prior_rendering,
                ) in lines[:index]:
                    if not (invisible or prior_invisible):
                        continue
                    if not _layer_line_bounds_overlap(bounds, prior_bounds):
                        continue
                    if (
                        rendering != prior_rendering
                        or _layer_line_texts_match(text, prior_text)
                    ):
                        duplicate_found = True
                        break
                if duplicate_found:
                    pages.append(page_index + 1)
                    break
    finally:
        document.close()
    return pages


def _extract_page_characters_from_rawdict(
    raw: dict,
    page_w: float,
    page_h: float,
    *,
    coordinate_transform: fitz.Matrix | None = None,
    page_x0: float = 0.0,
    page_y0: float = 0.0,
) -> list[dict]:
    """Convert a PyMuPDF rawdict page dict to displayed-page character boxes."""
    if page_w <= 0 or page_h <= 0:
        return []

    lines: list[list[dict]] = []

    for block in raw.get("blocks", []):
        if block.get("type") != 0:
            continue
        for line in block.get("lines", []):
            line_characters: list[dict] = []
            for span in line.get("spans", []):
                for ch in span.get("chars", []):
                    bbox = ch.get("bbox")
                    if not bbox or len(bbox) < 4:
                        continue

                    box = fitz.Rect(bbox)
                    if coordinate_transform is not None:
                        box = box * coordinate_transform
                    x0 = box.x0 - page_x0
                    y0 = box.y0 - page_y0
                    x1 = box.x1 - page_x0
                    y1 = box.y1 - page_y0
                    char = ch.get("c", "")
                    if not char:
                        continue

                    width = (x1 - x0) / page_w
                    height = (y1 - y0) / page_h
                    if width <= 0 or height <= 0:
                        continue

                    line_characters.append(
                        {
                            "char": char,
                            "x": x0 / page_w,
                            "y": y0 / page_h,
                            "width": width,
                            "height": height,
                            "lineIndex": len(lines),
                        }
                    )
            lines.append(line_characters)

    return _deduplicate_coincident_lines(lines)


def _extract_page_characters(page: fitz.Page) -> list[dict]:
    raw = page.get_text(
        "rawdict",
        flags=_RAWDICT_FLAGS,
        sort=page.rotation == 0,
    )
    page_rect = page.rect
    return _extract_page_characters_from_rawdict(
        raw,
        page_rect.width,
        page_rect.height,
        coordinate_transform=page.rotation_matrix if page.rotation else None,
        page_x0=page_rect.x0,
        page_y0=page_rect.y0,
    )


def extract_text_geometry(
    pdf_bytes: bytes,
    language: str = "eng",
    progress_callback: Callable[[str], None] | None = None,
    progress_label: str = "Extracting geometry",
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
                    f"{progress_label} page {page_index + 1} of {page_count}…"
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
