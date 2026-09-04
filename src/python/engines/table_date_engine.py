"""Recover dates from low-resolution ruled tables using cell-level OCR."""
from __future__ import annotations

import datetime as dt
import re
from collections import Counter

import pymupdf as fitz
from PIL import Image, ImageOps

_DATE_PATTERN = re.compile(r"\d{1,2}/\d{1,2}/\d{4}")


def normalize_date(raw: str) -> str | None:
    """Accept real slash dates with one- or two-digit months and days."""
    cleaned = re.sub(r"[^0-9/]", "", raw)
    digits = re.sub(r"\D", "", cleaned)
    if len(digits) == 8:
        cleaned = f"{digits[:2]}/{digits[2:4]}/{digits[4:]}"
    if not _DATE_PATTERN.fullmatch(cleaned):
        return None
    try:
        parsed = dt.datetime.strptime(cleaned, "%m/%d/%Y")
    except ValueError:
        return None
    if not 1900 <= parsed.year <= 2100:
        return None
    return cleaned


def _prepare_cell(
    cell: Image.Image,
    *,
    scale: int,
    threshold: int | None = None,
) -> Image.Image:
    prepared = ImageOps.autocontrast(ImageOps.grayscale(cell))
    if threshold is not None:
        prepared = prepared.point(lambda value: 0 if value < threshold else 255)
    prepared = prepared.resize(
        (prepared.width * scale, prepared.height * scale),
        Image.Resampling.LANCZOS,
    )
    padded = Image.new("L", (prepared.width + 32, prepared.height + 32), 255)
    padded.paste(prepared, (16, 16))
    return padded


def _ocr_text(cell: Image.Image, language: str, *, psm: int, scale: int,
              threshold: int | None = None, use_data: bool = False) -> str:
    import pytesseract

    prepared = _prepare_cell(cell, scale=scale, threshold=threshold)
    config = (
        f"--psm {psm} --dpi 600 "
        "-c tessedit_char_whitelist=0123456789/"
    )
    if use_data:
        data = pytesseract.image_to_data(
            prepared,
            lang=language,
            config=config,
            output_type=pytesseract.Output.DICT,
        )
        return "".join(str(text).strip() for text in data["text"] if str(text).strip())
    return pytesseract.image_to_string(
        prepared,
        lang=language,
        config=config,
    ).strip()


def _recognize_date(cell: Image.Image, language: str) -> str | None:
    """Run a bounded retry ladder and prefer agreement between valid readings."""
    variants = (
        (7, 8, None, True),
        (7, 10, None, False),
        (13, 10, None, False),
        (7, 10, 160, False),
        (13, 10, 160, False),
        (7, 10, 200, False),
        (13, 10, 200, False),
    )
    recognized: list[str] = []
    for psm, scale, threshold, use_data in variants:
        date = normalize_date(
            _ocr_text(
                cell,
                language,
                psm=psm,
                scale=scale,
                threshold=threshold,
                use_data=use_data,
            )
        )
        if date:
            recognized.append(date)
            if recognized.count(date) >= 2:
                return date
    if not recognized:
        return None
    counts = Counter(recognized)
    return max(counts, key=lambda date: (counts[date], -recognized.index(date)))


def _map_image_rect(
    placement: fitz.Rect,
    image_size: tuple[int, int],
    pixel_rect: tuple[float, float, float, float],
) -> fitz.Rect:
    image_width, image_height = image_size
    x0, y0, x1, y1 = pixel_rect
    return fitz.Rect(
        placement.x0 + x0 / image_width * placement.width,
        placement.y0 + y0 / image_height * placement.height,
        placement.x0 + x1 / image_width * placement.width,
        placement.y0 + y1 / image_height * placement.height,
    )
