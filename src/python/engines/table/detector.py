"""Orchestrate OCR-adjacent table structure detection."""
from __future__ import annotations

import pymupdf as fitz

from engines.binary_codec import json_to_base64
from engines.table.columns import detect_columns
from engines.table.headers import detect_header
from engines.table.regions import discover_regions
from engines.table.rows import detect_rows
from engines.table.rulings import detect_page_rulings


def detect_tables(
    pdf_bytes: bytes,
    geometry: dict,
    progress_callback=None,
) -> dict:
    geometry_by_page = {int(page["pageIndex"]): page for page in geometry.get("pages", [])}
    pages: list[dict] = []
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page_index in range(doc.page_count):
            if progress_callback:
                progress_callback(f"Detecting table structure page {page_index + 1} of {doc.page_count}…")
            page_geometry = geometry_by_page.get(page_index, {"pageIndex": page_index, "characters": []})
            vertical, horizontal = detect_page_rulings(doc.load_page(page_index))
            tables: list[dict] = []
            for candidate in discover_regions(page_geometry, vertical, horizontal):
                bounds = candidate["bounds"]
                columns = detect_columns(page_geometry, bounds, vertical)
                if len(columns) < 2:
                    continue
                rows = detect_rows(page_geometry, bounds, horizontal, columns)
                if len(rows) < 2:
                    continue
                header = detect_header(
                    page_geometry,
                    columns,
                    rows,
                    ruled=candidate["evidence"] == "ruled",
                )
                tables.append({
                    "id": f"page-{page_index}-table-{len(tables)}",
                    "bounds": bounds,
                    "evidence": candidate["evidence"],
                    "confidence": candidate["confidence"],
                    "columns": columns,
                    "rows": rows,
                    "header": header,
                    "rulings": {
                        "vertical": [value for value in vertical if bounds["x"] - 0.002 <= value <= bounds["x"] + bounds["width"] + 0.002],
                        "horizontal": [value for value in horizontal if bounds["y"] - 0.002 <= value <= bounds["y"] + bounds["height"] + 0.002],
                    },
                })
            pages.append({"pageIndex": page_index, "tables": tables})
    finally:
        doc.close()
    return {"version": 1, "coordinateSpace": "normalized", "pages": pages}


def structure_to_base64(structure: dict) -> str:
    return json_to_base64(structure)
