"""Column-aware OCR and text-layer rebuilding for low-resolution ruled tables."""
from __future__ import annotations

import io
import re
from collections import Counter
from difflib import SequenceMatcher
from typing import Callable

import pymupdf as fitz
from PIL import Image, ImageDraw, ImageFilter, ImageOps

from engines.ocr_engine import configure_tesseract
from engines.table_date_engine import (
    _date_columns,
    _map_image_rect,
    _prepare_cell,
    _recognize_date,
    detect_ruled_grid,
)


_STATUS_PHRASES = (
    "Closure Permanent",
    "Closure Temporary",
    "Layoff Permanent",
    "Layoff Temporary",
    "Layoff Unknown at this time",
)

_HEADER_LINE_LAYOUTS = {
    "Effective Date": ("Effective", "Date"),
    "Received Date": ("Received", "Date"),
    "No. Of Employees": ("No. Of", "Employees"),
}

_MIN_TABLE_IMAGE_PAGE_COVERAGE = 0.05


def _largest_table_placement(
    page_rect: fitz.Rect,
    placements: list[fitz.Rect],
) -> fitz.Rect | None:
    """Return the largest placement only when it could plausibly hold a table."""
    page_area = page_rect.get_area()
    if page_area <= 0 or not placements:
        return None
    placement = max(placements, key=lambda rect: rect.get_area())
    if placement.get_area() / page_area < _MIN_TABLE_IMAGE_PAGE_COVERAGE:
        return None
    return placement


def _clean_grid(image: Image.Image, x_lines: list[int], y_lines: list[int]) -> Image.Image:
    clean = ImageOps.grayscale(image).copy()
    draw = ImageDraw.Draw(clean)
    for x in x_lines:
        draw.rectangle(
            (x - 2, y_lines[0] - 1, x + 2, y_lines[-1] + 1),
            fill=255,
        )
    for y in y_lines:
        draw.rectangle(
            (x_lines[0] - 1, y - 2, x_lines[-1] + 1, y + 2),
            fill=255,
        )
    return clean


def _ink_line_boxes(
    image: Image.Image,
    *,
    threshold: int = 200,
) -> list[tuple[int, int, int, int]]:
    """Return tight foreground boxes for each printed text line in a cell."""
    gray = ImageOps.grayscale(image)
    pixels = gray.load()
    active_rows = [
        y
        for y in range(gray.height)
        if any(pixels[x, y] < threshold for x in range(gray.width))
    ]
    if not active_rows:
        return []

    row_groups: list[list[int]] = [[active_rows[0]]]
    for y in active_rows[1:]:
        if y == row_groups[-1][-1] + 1:
            row_groups[-1].append(y)
        else:
            row_groups.append([y])

    boxes: list[tuple[int, int, int, int]] = []
    for rows in row_groups:
        y0 = rows[0]
        y1 = rows[-1] + 1
        columns = [
            x
            for x in range(gray.width)
            if any(pixels[x, y] < threshold for y in rows)
        ]
        dark_pixels = sum(
            pixels[x, y] < threshold
            for x in columns
            for y in rows
        )
        if not columns or dark_pixels < 2:
            continue
        boxes.append((columns[0], y0, columns[-1] + 1, y1))
    return boxes


def _ink_box(image: Image.Image) -> tuple[int, int, int, int] | None:
    boxes = _ink_line_boxes(image)
    if not boxes:
        return None
    return (
        min(box[0] for box in boxes),
        min(box[1] for box in boxes),
        max(box[2] for box in boxes),
        max(box[3] for box in boxes),
    )


def _header_line_texts(text: str, line_count: int) -> list[str]:
    if line_count <= 1:
        return [text]
    preferred = _HEADER_LINE_LAYOUTS.get(text)
    if preferred and len(preferred) == line_count:
        return list(preferred)

    words = text.split()
    if len(words) < line_count:
        return [text]
    return [
        " ".join(words[start:end])
        for start, end in (
            (
                round(index * len(words) / line_count),
                round((index + 1) * len(words) / line_count),
            )
            for index in range(line_count)
        )
    ]


def _map_cell_segments(
    placement: fitz.Rect,
    image_size: tuple[int, int],
    cell_origin: tuple[int, int],
    cell_image: Image.Image,
    texts: list[str],
) -> list[dict]:
    boxes = _ink_line_boxes(cell_image)
    if len(boxes) != len(texts):
        union = _ink_box(cell_image)
        boxes = [union] if union else []
        texts = [" ".join(texts)]
    origin_x, origin_y = cell_origin
    return [
        {
            "text": text,
            "rect": _map_image_rect(
                placement,
                image_size,
                (
                    origin_x + box[0],
                    origin_y + box[1],
                    origin_x + box[2],
                    origin_y + box[3],
                ),
            ),
        }
        for text, box in zip(texts, boxes)
        if text
    ]


def _cleanup_text(text: str) -> str:
    cleaned = text.replace("�", " ").replace("—", "-").replace("–", "-")
    cleaned = re.sub(r"\s+", " ", cleaned).strip(" |_-")
    cleaned = re.sub(r"\s+([,.;:])", r"\1", cleaned)
    cleaned = re.sub(r"\.\s+(Inc\.|LLC\b)", r", \1", cleaned)
    cleaned = re.sub(r"\bd/o?b/a\s*", "d/b/a ", cleaned, flags=re.IGNORECASE)
    cleaned = re.sub(r"\bd/b/a(?=[A-Z])", "d/b/a ", cleaned)
    cleaned = re.sub(r"\b([A-Z])\.([A-Z])\.1\.", r"\1.\2.I.", cleaned)
    cleaned = re.sub(r"bura\b", "burg", cleaned, flags=re.IGNORECASE)
    if cleaned.endswith(")") and "(" not in cleaned:
        cleaned = cleaned[:-1].rstrip()
    return cleaned.strip()


def _ocr_candidate(
    cell: Image.Image,
    language: str,
    *,
    scale: int = 10,
    threshold: int | None = None,
    sharpen: bool = False,
    digits_only: bool = False,
) -> tuple[str, float]:
    import pytesseract

    prepared = ImageOps.autocontrast(ImageOps.grayscale(cell))
    if sharpen:
        prepared = prepared.filter(ImageFilter.SHARPEN)
    if threshold is not None:
        prepared = prepared.point(lambda value: 0 if value < threshold else 255)
    prepared = prepared.resize(
        (prepared.width * scale, prepared.height * scale),
        Image.Resampling.LANCZOS,
    )
    padded = Image.new("L", (prepared.width + 40, prepared.height + 40), 255)
    padded.paste(prepared, (20, 20))
    config = "--psm 7 --dpi 600 -c preserve_interword_spaces=1"
    if digits_only:
        config += " -c tessedit_char_whitelist=0123456789"
    data = pytesseract.image_to_data(
        padded,
        lang=language,
        config=config,
        output_type=pytesseract.Output.DICT,
    )
    words: list[str] = []
    confidences: list[float] = []
    for raw_text, raw_confidence in zip(data["text"], data["conf"]):
        text = str(raw_text).strip()
        if not text:
            continue
        words.append(text)
        try:
            confidence = float(raw_confidence)
        except (TypeError, ValueError):
            continue
        if confidence >= 0:
            confidences.append(confidence)
    text = _cleanup_text(" ".join(words))
    mean_confidence = sum(confidences) / len(confidences) if confidences else -1.0
    return text, mean_confidence


def _cell_candidates(
    raw_cell: Image.Image,
    clean_cell: Image.Image,
    language: str,
    *,
    kind: str,
    digits_only: bool = False,
) -> list[dict]:
    if kind == "status":
        variants = [("raw", raw_cell, 10, None, False)]
    elif kind == "number":
        variants = [
            ("raw", raw_cell, 10, None, False),
            ("raw-threshold", raw_cell, 10, 160, False),
            ("clean", clean_cell, 10, None, False),
        ]
    else:
        variants = [
            ("raw", raw_cell, 10, None, False),
            ("clean", clean_cell, 10, None, False),
        ]

    candidates: list[dict] = []

    def append_variant(variant: tuple[str, Image.Image, int, int | None, bool]) -> None:
        source, image, scale, threshold, sharpen = variant
        text, confidence = _ocr_candidate(
            image,
            language,
            scale=scale,
            threshold=threshold,
            sharpen=sharpen,
            digits_only=digits_only,
        )
        if text:
            candidates.append(
                {"source": source, "text": text, "confidence": confidence}
            )

    for variant in variants:
        append_variant(variant)

    best_confidence = max(
        (candidate["confidence"] for candidate in candidates),
        default=-1,
    )
    if kind == "number" and (best_confidence < 80 or len({c["text"] for c in candidates}) > 1):
        retries = (
            ("raw-small", raw_cell, 8, None, False),
            ("clean-small", clean_cell, 8, None, False),
            ("clean-sharp", clean_cell, 10, None, True),
        )
    elif kind == "city" and (
        best_confidence < 85 or len({c["text"] for c in candidates}) > 1
    ):
        retries = (
            ("raw-small", raw_cell, 8, None, False),
            ("clean-small", clean_cell, 8, None, False),
        )
    elif kind == "text" and best_confidence < 50:
        retries = (
            ("raw-small", raw_cell, 8, None, False),
            ("raw-threshold", raw_cell, 10, 160, False),
            ("clean-small", clean_cell, 8, None, False),
            ("clean-sharp", clean_cell, 10, None, True),
        )
    elif kind == "status" and not candidates:
        retries = (("clean", clean_cell, 10, None, False),)
    else:
        retries = ()
    for variant in retries:
        append_variant(variant)
    return candidates


def _select_number(candidates: list[dict]) -> str | None:
    numeric = [candidate for candidate in candidates if candidate["text"].isdigit()]
    if not numeric:
        return None
    scores: dict[str, float] = {}
    for text in {candidate["text"] for candidate in numeric}:
        matching = [candidate for candidate in numeric if candidate["text"] == text]
        scores[text] = max(candidate["confidence"] for candidate in matching) + len(matching) * 0.5
    return max(scores, key=lambda text: (scores[text], len(text)))


def _select_status(candidates: list[dict]) -> str | None:
    if not candidates:
        return None
    candidate = max(candidates, key=lambda item: item["confidence"])["text"]
    normalized = candidate.lower()
    return max(
        _STATUS_PHRASES,
        key=lambda phrase: SequenceMatcher(None, normalized, phrase.lower()).ratio(),
    )


def _looks_noisy(text: str) -> bool:
    return not text or bool(re.search(r"[_|�]", text))


def _select_city(candidates: list[dict]) -> str | None:
    if not candidates:
        return None
    counts = Counter(candidate["text"] for candidate in candidates)

    def score(text: str) -> tuple[int, int, float]:
        matching = [candidate for candidate in candidates if candidate["text"] == text]
        has_raw = any(candidate["source"].startswith("raw") for candidate in matching)
        return (
            counts[text],
            1 if has_raw else 0,
            max(candidate["confidence"] for candidate in matching),
        )

    return max(counts, key=score)


def _select_general_text(candidates: list[dict]) -> str | None:
    if not candidates:
        return None
    raw = next((candidate for candidate in candidates if candidate["source"] == "raw"), None)
    if raw and not _looks_noisy(raw["text"]):
        return raw["text"]
    return max(candidates, key=lambda item: (item["confidence"], len(item["text"])))["text"]


def _normalized_key(text: str) -> str:
    return re.sub(r"[^a-z0-9]", "", text.lower())


def _canonicalize_repeated_cells(cells: list[dict]) -> None:
    """Use repeated values in a text column to repair faint or confused glyphs."""
    groups: list[list[dict]] = []
    for cell in cells:
        text = cell.get("text") or ""
        if not text:
            continue
        key = _normalized_key(text)
        for group in groups:
            representative = _normalized_key(group[0]["text"])
            if SequenceMatcher(None, key, representative).ratio() >= 0.82:
                group.append(cell)
                break
        else:
            groups.append([cell])

    for group in groups:
        if len(group) < 3:
            continue
        pool = [
            candidate["text"]
            for cell in group
            for candidate in cell.get("candidates", [])
            if candidate["source"].startswith("clean") and candidate["text"]
        ]
        if not pool:
            continue
        normalized_counts = Counter(_normalized_key(text) for text in pool)
        winning_key = max(normalized_counts, key=normalized_counts.get)
        spellings = [text for text in pool if _normalized_key(text) == winning_key]
        canonical = Counter(spellings).most_common(1)[0][0]
        for cell in group:
            if SequenceMatcher(
                None,
                _normalized_key(cell["text"]),
                winning_key,
            ).ratio() >= 0.80:
                cell["text"] = canonical


def _canonical_header(text: str) -> str:
    normalized = _normalized_key(text)
    if "notice" in normalized and "date" in normalized:
        return "Notice Date"
    if "effective" in normalized and "date" in normalized:
        return "Effective Date"
    if "received" in normalized and "date" in normalized:
        return "Received Date"
    if "company" in normalized:
        return "Company"
    if normalized == "city" or normalized.startswith("city"):
        return "City"
    if "employee" in normalized:
        return "No. Of Employees"
    if "layoff" in normalized and "closure" in normalized:
        return "Layoff/Closure"
    return text


def _column_kind(header: str) -> str:
    normalized = _normalized_key(header)
    if "date" in normalized:
        return "date"
    if "employee" in normalized or normalized.startswith("noof"):
        return "number"
    if "layoff" in normalized or "closure" in normalized:
        return "status"
    if "city" in normalized:
        return "city"
    return "text"


def _cleanup_sparse_word(text: str) -> str | None:
    raw = text.strip()
    if raw in {"-", "—", "–"}:
        return "-"
    if not any(character.isalnum() for character in raw):
        return None
    if re.fullmatch(r"10\D*", raw):
        return "10th"
    if re.fullmatch(r"25\D*", raw):
        return "25th"
    cleaned = raw.replace("�", "")
    return cleaned or None


def _recognize_sparse_region(
    gray: Image.Image,
    placement: fitz.Rect,
    image_size: tuple[int, int],
    pixel_rect: tuple[int, int, int, int],
    language: str,
) -> list[dict]:
    import pytesseract

    region = ImageOps.autocontrast(gray.crop(pixel_rect))
    scale = 8
    prepared = region.resize(
        (region.width * scale, region.height * scale),
        Image.Resampling.LANCZOS,
    )
    data = pytesseract.image_to_data(
        prepared,
        lang=language,
        config="--psm 11 --dpi 600",
        output_type=pytesseract.Output.DICT,
    )
    region_x, region_y = pixel_rect[0], pixel_rect[1]
    items: list[dict] = []
    for index, raw_text in enumerate(data["text"]):
        text = _cleanup_sparse_word(str(raw_text))
        if not text:
            continue
        try:
            confidence = float(data["conf"][index])
        except (TypeError, ValueError):
            continue
        if confidence < 0:
            continue
        left = region_x + int(data["left"][index]) / scale
        top = region_y + int(data["top"][index]) / scale
        right = left + int(data["width"][index]) / scale
        bottom = top + int(data["height"][index]) / scale
        items.append(
            {
                "kind": "page-text",
                "text": text,
                "rect": _map_image_rect(
                    placement,
                    image_size,
                    (left, top, right, bottom),
                ),
            }
        )
    return items


def _insert_invisible_cell(page: fitz.Page, rect: fitz.Rect, text: str) -> None:
    font = fitz.Font("helv")
    metric_height = font.ascender - font.descender
    font_size = rect.height / metric_height
    natural_width = max(
        0.1,
        fitz.get_text_length(text, fontname="helv", fontsize=font_size),
    )
    horizontal_scale = rect.width / natural_width
    origin = fitz.Point(rect.x0, rect.y0 + font.ascender * font_size)
    page.insert_text(
        origin,
        text,
        fontsize=font_size,
        fontname="helv",
        render_mode=3,
        morph=(origin, fitz.Matrix(horizontal_scale, 1.0)),
        overlay=True,
    )


def has_recoverable_ruled_table(
    pdf_bytes: bytes,
    language: str = "eng",
) -> tuple[bool, dict]:
    """Cheaply detect a ruled table that requires the legacy cell-recovery path."""
    stats = {
        "table_images_examined": 0,
        "table_images_skipped_small": 0,
        "table_grid_candidates": 0,
    }
    configure_tesseract()
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page in doc:
            seen_xrefs: set[int] = set()
            for image_info in page.get_images(full=True):
                xref = int(image_info[0])
                if xref in seen_xrefs:
                    continue
                seen_xrefs.add(xref)
                stats["table_images_examined"] += 1
                placement = _largest_table_placement(
                    page.rect,
                    page.get_image_rects(xref),
                )
                if placement is None:
                    stats["table_images_skipped_small"] += 1
                    continue
                try:
                    image = Image.open(
                        io.BytesIO(doc.extract_image(xref)["image"])
                    ).convert("RGB")
                except (KeyError, OSError):
                    continue
                x_lines, y_lines = detect_ruled_grid(image)
                if not x_lines or not y_lines:
                    continue
                stats["table_grid_candidates"] += 1
                if _date_columns(
                    ImageOps.grayscale(image),
                    x_lines,
                    y_lines,
                    language,
                ):
                    return True, stats
        return False, stats
    finally:
        doc.close()


def recover_table_cells(
    source_pdf_bytes: bytes,
    ocr_pdf_bytes: bytes,
    language: str = "eng",
    progress_callback: Callable[[str], None] | None = None,
) -> tuple[bytes, dict]:
    """Recognize every cell in strongly ruled tables and rebuild their text layer."""
    stats = {
        "date_tables_detected": 0,
        "date_cells_detected": 0,
        "date_cells_resolved": 0,
        "date_cells_unresolved": 0,
        "table_cells_detected": 0,
        "table_cells_resolved": 0,
        "table_cells_unresolved": 0,
        "table_images_examined": 0,
        "table_images_skipped_small": 0,
        "table_grid_candidates": 0,
        "page_text_regions_detected": 0,
        "page_text_words_resolved": 0,
        "changed": False,
    }
    configure_tesseract()
    source_doc = fitz.open(stream=source_pdf_bytes, filetype="pdf")
    output_doc = fitz.open(stream=ocr_pdf_bytes, filetype="pdf")
    page_tables: dict[int, list[dict]] = {}
    try:
        for page_index in range(min(source_doc.page_count, output_doc.page_count)):
            source_page = source_doc.load_page(page_index)
            seen_xrefs: set[int] = set()
            for image_info in source_page.get_images(full=True):
                xref = int(image_info[0])
                if xref in seen_xrefs:
                    continue
                seen_xrefs.add(xref)
                stats["table_images_examined"] += 1
                placements = source_page.get_image_rects(xref)
                placement = _largest_table_placement(source_page.rect, placements)
                if placement is None:
                    stats["table_images_skipped_small"] += 1
                    continue
                try:
                    image = Image.open(
                        io.BytesIO(source_doc.extract_image(xref)["image"])
                    ).convert("RGB")
                except (KeyError, OSError):
                    continue
                x_lines, y_lines = detect_ruled_grid(image)
                if not x_lines or not y_lines:
                    continue
                stats["table_grid_candidates"] += 1
                gray = ImageOps.grayscale(image)
                date_header = _date_columns(gray, x_lines, y_lines, language)
                if not date_header:
                    continue
                header_row, date_columns = date_header
                clean = _clean_grid(gray, x_lines, y_lines)
                first_data_row = header_row + 1
                column_count = len(x_lines) - 1
                if first_data_row >= len(y_lines) - 1:
                    continue

                if progress_callback:
                    progress_callback(
                        f"Recovering {column_count}-column ruled table "
                        f"on page {page_index + 1}…"
                    )

                headers: list[dict] = []
                for column in range(column_count):
                    header_pixel_rect = (
                        x_lines[column],
                        y_lines[header_row],
                        x_lines[column + 1],
                        y_lines[header_row + 1],
                    )
                    raw_header = gray.crop(
                        (
                            x_lines[column] + 1,
                            y_lines[header_row] + 1,
                            x_lines[column + 1],
                            y_lines[header_row + 1],
                        )
                    )
                    import pytesseract

                    header_text = pytesseract.image_to_string(
                        _prepare_cell(raw_header, scale=8),
                        lang=language,
                        config="--psm 6 --dpi 600",
                    )
                    text = _canonical_header(_cleanup_text(header_text))
                    clean_header = clean.crop(header_pixel_rect)
                    header_boxes = _ink_line_boxes(clean_header)
                    line_texts = _header_line_texts(text, len(header_boxes))
                    rect = _map_image_rect(
                        placement,
                        image.size,
                        (
                            x_lines[column] + 1,
                            y_lines[header_row] + 1,
                            x_lines[column + 1],
                            y_lines[header_row + 1],
                        ),
                    )
                    headers.append(
                        {
                            "column": column,
                            "text": text or None,
                            "kind": _column_kind(text),
                            "rect": rect,
                            "segments": _map_cell_segments(
                                placement,
                                image.size,
                                (x_lines[column], y_lines[header_row]),
                                clean_header,
                                line_texts,
                            ),
                        }
                    )

                cells: list[dict] = []
                for row in range(first_data_row, len(y_lines) - 1):
                    for header in headers:
                        column = header["column"]
                        pixel_rect = (
                            x_lines[column] + 1,
                            y_lines[row] + 1,
                            x_lines[column + 1],
                            y_lines[row + 1],
                        )
                        raw_cell = gray.crop(pixel_rect)
                        clean_cell = clean.crop(
                            (
                                x_lines[column],
                                y_lines[row],
                                x_lines[column + 1],
                                y_lines[row + 1],
                            )
                        )
                        kind = header["kind"]
                        candidates: list[dict] = []
                        if kind == "date":
                            text = _recognize_date(raw_cell, language)
                            stats["date_cells_detected"] += 1
                            if text:
                                stats["date_cells_resolved"] += 1
                            else:
                                stats["date_cells_unresolved"] += 1
                        else:
                            candidates = _cell_candidates(
                                raw_cell,
                                clean_cell,
                                language,
                                kind=kind,
                                digits_only=kind == "number",
                            )
                            if kind == "number":
                                text = _select_number(candidates)
                            elif kind == "status":
                                text = _select_status(candidates)
                            elif kind == "city":
                                text = _select_city(candidates)
                            else:
                                text = _select_general_text(candidates)
                        cells.append(
                            {
                                "row": row,
                                "column": column,
                                "kind": kind,
                                "text": text,
                                "candidates": candidates,
                                "rect": _map_image_rect(
                                    placement,
                                    image.size,
                                    pixel_rect,
                                ),
                                "segments": _map_cell_segments(
                                    placement,
                                    image.size,
                                    (x_lines[column], y_lines[row]),
                                    clean_cell,
                                    [text] if text else [],
                                ),
                            }
                        )

                for header in headers:
                    if header["kind"] == "text":
                        _canonicalize_repeated_cells(
                            [cell for cell in cells if cell["column"] == header["column"]]
                        )
                for cell in cells:
                    if cell["segments"]:
                        cell["segments"][0]["text"] = cell["text"]

                table_items = headers + cells
                region_items: list[dict] = []
                region_rect: fitz.Rect | None = None
                table_top = y_lines[header_row]
                if 20 <= table_top <= image.height * 0.35:
                    region_pixel_rect = (0, 0, image.width, table_top)
                    region_items = _recognize_sparse_region(
                        gray,
                        placement,
                        image.size,
                        region_pixel_rect,
                        language,
                    )
                    if region_items:
                        region_rect = _map_image_rect(
                            placement,
                            image.size,
                            region_pixel_rect,
                        )
                        stats["page_text_regions_detected"] += 1
                        stats["page_text_words_resolved"] += len(region_items)
                resolved = sum(bool(item["text"]) for item in table_items)
                stats["date_tables_detected"] += 1
                stats["table_cells_detected"] += len(table_items)
                stats["table_cells_resolved"] += resolved
                stats["table_cells_unresolved"] += len(table_items) - resolved
                page_tables.setdefault(page_index, []).append(
                    {
                        "placement": placement,
                        "image_size": image.size,
                        "x_lines": x_lines,
                        "y_lines": y_lines,
                        "header_row": header_row,
                        "items": table_items,
                        "region_items": region_items,
                        "region_rect": region_rect,
                    }
                )

        if not page_tables:
            return ocr_pdf_bytes, stats

        redacted_pages: set[int] = set()
        for page_index, tables in page_tables.items():
            page = output_doc.load_page(page_index)
            for table in tables:
                items = table["items"]
                resolution = sum(bool(item["text"]) for item in items) / len(items)
                if resolution < 0.98:
                    continue
                rect = _map_image_rect(
                    table["placement"],
                    table["image_size"],
                    (
                        table["x_lines"][0] - 3,
                        table["y_lines"][table["header_row"]] - 3,
                        table["x_lines"][-1] + 3,
                        table["y_lines"][-1] + 3,
                    ),
                )
                page.add_redact_annot(rect, fill=False, cross_out=False)
                redacted_pages.add(page_index)
                if table["region_rect"] is not None:
                    page.add_redact_annot(
                        table["region_rect"],
                        fill=False,
                        cross_out=False,
                    )

        for page_index in redacted_pages:
            output_doc.load_page(page_index).apply_redactions(
                images=0,
                graphics=0,
                text=0,
            )

        for page_index, tables in page_tables.items():
            page = output_doc.load_page(page_index)
            for table in tables:
                for item in table["region_items"]:
                    _insert_invisible_cell(
                        page,
                        item["rect"],
                        item["text"],
                    )
                for item in table["items"]:
                    text = item["text"]
                    if text:
                        for segment in item["segments"]:
                            _insert_invisible_cell(
                                page,
                                segment["rect"],
                                segment["text"],
                            )

        stats["changed"] = True
        return output_doc.tobytes(garbage=4, deflate=True), stats
    finally:
        source_doc.close()
        output_doc.close()
