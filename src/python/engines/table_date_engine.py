"""Recover dates from low-resolution ruled tables using cell-level OCR."""
from __future__ import annotations

import datetime as dt
import io
import re
from collections.abc import Callable

import pymupdf as fitz
from PIL import Image, ImageOps

from engines.ocr_engine import configure_tesseract


_GRID_DARKNESS = 110
_MIN_IMAGE_WIDTH = 400
_MIN_IMAGE_HEIGHT = 200
_DATE_PATTERN = re.compile(r"\d{2}/\d{2}/\d{4}")


def _pixel_values(image: Image.Image) -> list[int]:
    """Read a one-band image across supported Pillow versions without warnings."""
    flattened = getattr(image, "get_flattened_data", None)
    return list(flattened() if flattened is not None else image.getdata())


def _line_centers(counts: list[int], minimum: int) -> list[int]:
    """Collapse adjacent qualifying pixels into one grid-line coordinate."""
    runs: list[list[int]] = []
    for index, count in enumerate(counts):
        if count < minimum:
            continue
        if not runs or index > runs[-1][-1] + 1:
            runs.append([index])
        else:
            runs[-1].append(index)
    return [round(sum(run) / len(run)) for run in runs]


def detect_ruled_grid(image: Image.Image) -> tuple[list[int], list[int]]:
    """Return strong full-table vertical and horizontal line centers."""
    gray = ImageOps.grayscale(image)
    width, height = gray.size
    if width < _MIN_IMAGE_WIDTH or height < _MIN_IMAGE_HEIGHT:
        return [], []

    # Project a binary dark-pixel mask down to one row / column. Pillow's BOX
    # resampler performs the averaging in native code; the former nested Python
    # pixel loops took several seconds per high-resolution scan and hundreds of
    # seconds across a long document even when no grid existed.
    dark = gray.point(lambda value: 255 if value < _GRID_DARKNESS else 0)
    vertical_density = _pixel_values(
        dark.resize((width, 1), Image.Resampling.BOX)
    )
    horizontal_density = _pixel_values(
        dark.resize((1, height), Image.Resampling.BOX)
    )
    x_lines = _line_centers(vertical_density, round(255 * 0.70))
    y_lines = _line_centers(horizontal_density, round(255 * 0.50))
    if len(x_lines) < 3 or len(y_lines) < 3:
        return [], []
    return x_lines, y_lines


def normalize_date(raw: str) -> str | None:
    """Accept only real MM/DD/YYYY dates in the expected document-era range."""
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
    """Run a bounded retry ladder and return the first validated date."""
    variants = (
        (7, 8, None, True),
        (7, 10, None, False),
        (13, 10, None, False),
        (7, 10, 160, False),
        (13, 10, 160, False),
        (7, 10, 200, False),
        (13, 10, 200, False),
    )
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
            return date
    return None


def _date_columns(
    gray: Image.Image,
    x_lines: list[int],
    y_lines: list[int],
    language: str,
) -> tuple[int, list[int]] | None:
    """Find a header row and columns whose recognized header contains 'date'."""
    import pytesseract

    candidate_rows = min(3, len(y_lines) - 1)
    for row in range(candidate_rows):
        columns: list[int] = []
        for column in range(len(x_lines) - 1):
            cell = gray.crop(
                (
                    x_lines[column] + 1,
                    y_lines[row] + 1,
                    x_lines[column + 1],
                    y_lines[row + 1],
                )
            )
            header = pytesseract.image_to_string(
                _prepare_cell(cell, scale=8),
                lang=language,
                config="--psm 6 --dpi 600",
            )
            normalized = re.sub(r"[^a-z]", "", header.lower())
            if "date" in normalized:
                columns.append(column)
        if columns:
            return row, columns
    return None


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


def _insert_invisible_date(page: fitz.Page, rect: fitz.Rect, date: str) -> None:
    font_size = max(2.5, min(8.0, rect.height * 0.62))
    text_width = fitz.get_text_length(date, fontname="helv", fontsize=font_size)
    x = rect.x0 + max(0.2, (rect.width - text_width) / 2)
    baseline_y = rect.y0 + rect.height * 0.80
    page.insert_text(
        fitz.Point(x, baseline_y),
        date,
        fontsize=font_size,
        fontname="helv",
        render_mode=3,
        overlay=True,
    )


def recover_table_dates(
    source_pdf_bytes: bytes,
    ocr_pdf_bytes: bytes,
    language: str = "eng",
    progress_callback: Callable[[str], None] | None = None,
) -> tuple[bytes, dict]:
    """
    Recover validated dates from ruled table cells and rebuild their text layer.

    The source PDF supplies unmodified scan pixels. The OCR PDF supplies the
    selected full-page OCR result. Fully resolved date columns have only their
    existing invisible text removed; images and line art are retained exactly.
    Corrected dates are then inserted with PDF text rendering mode 3 (invisible).
    """
    stats = {
        "date_tables_detected": 0,
        "date_cells_detected": 0,
        "date_cells_resolved": 0,
        "date_cells_unresolved": 0,
        "changed": False,
    }
    configure_tesseract()

    source_doc = fitz.open(stream=source_pdf_bytes, filetype="pdf")
    output_doc = fitz.open(stream=ocr_pdf_bytes, filetype="pdf")
    page_tables: dict[int, list[dict]] = {}
    try:
        page_count = min(source_doc.page_count, output_doc.page_count)
        for page_index in range(page_count):
            source_page = source_doc.load_page(page_index)
            seen_xrefs: set[int] = set()
            for image_info in source_page.get_images(full=True):
                xref = int(image_info[0])
                if xref in seen_xrefs:
                    continue
                seen_xrefs.add(xref)

                extracted = source_doc.extract_image(xref)
                try:
                    image = Image.open(io.BytesIO(extracted["image"])).convert("RGB")
                except (KeyError, OSError):
                    continue
                x_lines, y_lines = detect_ruled_grid(image)
                if not x_lines or not y_lines:
                    continue

                gray = ImageOps.grayscale(image)
                header = _date_columns(gray, x_lines, y_lines, language)
                if not header:
                    continue
                header_row, columns = header
                first_data_row = header_row + 1
                if first_data_row >= len(y_lines) - 1:
                    continue

                placements = source_page.get_image_rects(xref)
                if not placements:
                    continue
                placement = placements[0]
                stats["date_tables_detected"] += 1
                cells: list[dict] = []
                total_cells = (len(y_lines) - 1 - first_data_row) * len(columns)
                if progress_callback:
                    progress_callback(
                        f"Recovering {total_cells} ruled-table date cells "
                        f"on page {page_index + 1}…"
                    )

                for row in range(first_data_row, len(y_lines) - 1):
                    for column in columns:
                        pixel_rect = (
                            x_lines[column] + 1,
                            y_lines[row] + 1,
                            x_lines[column + 1],
                            y_lines[row + 1],
                        )
                        cell_image = gray.crop(pixel_rect)
                        date = _recognize_date(cell_image, language)
                        page_rect = _map_image_rect(
                            placement,
                            image.size,
                            pixel_rect,
                        )
                        cells.append(
                            {
                                "row": row,
                                "column": column,
                                "date": date,
                                "rect": page_rect,
                            }
                        )
                        stats["date_cells_detected"] += 1
                        if date:
                            stats["date_cells_resolved"] += 1
                        else:
                            stats["date_cells_unresolved"] += 1

                page_tables.setdefault(page_index, []).append(
                    {
                        "placement": placement,
                        "image_size": image.size,
                        "x_lines": x_lines,
                        "y_lines": y_lines,
                        "first_data_row": first_data_row,
                        "columns": columns,
                        "cells": cells,
                    }
                )

        if not stats["date_cells_resolved"]:
            return ocr_pdf_bytes, stats

        # First remove stale OCR only where an entire date column was recovered.
        # Partial columns retain their old text to avoid losing unresolved data.
        redacted_pages: set[int] = set()
        for page_index, tables in page_tables.items():
            page = output_doc.load_page(page_index)
            for table in tables:
                cells = table["cells"]
                for column in table["columns"]:
                    column_cells = [cell for cell in cells if cell["column"] == column]
                    if not column_cells or any(not cell["date"] for cell in column_cells):
                        continue
                    x_lines = table["x_lines"]
                    y_lines = table["y_lines"]
                    removal_rect = _map_image_rect(
                        table["placement"],
                        table["image_size"],
                        (
                            x_lines[column] - 3,
                            y_lines[table["first_data_row"]] - 2,
                            x_lines[column + 1] + 3,
                            y_lines[-1] + 2,
                        ),
                    )
                    page.add_redact_annot(removal_rect, fill=False, cross_out=False)
                    redacted_pages.add(page_index)

        for page_index in redacted_pages:
            output_doc.load_page(page_index).apply_redactions(
                images=0,
                graphics=0,
                text=0,
            )

        for page_index, tables in page_tables.items():
            page = output_doc.load_page(page_index)
            for table in tables:
                for cell in table["cells"]:
                    if cell["date"]:
                        _insert_invisible_date(page, cell["rect"], cell["date"])

        stats["changed"] = True
        return output_doc.tobytes(garbage=4, deflate=True), stats
    finally:
        source_doc.close()
        output_doc.close()
