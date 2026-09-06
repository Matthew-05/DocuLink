"""Orchestrate OCR-adjacent table structure detection.

Detection is optional work layered on top of a finished OCR result, so it runs
under a wall-clock budget: a pathological document may not spend the whole OCR
job inside the detector. Pages already detected are kept and the model records
that it stopped early.
"""
from __future__ import annotations

import os
import time

import pymupdf as fitz

from engines.binary_codec import json_to_base64
from schemas.models import Stage
from engines.table.redesign import DETECTOR_VERSION, PERIOD_ANALYSIS, detect_page


# Wall-clock ceiling for detecting one document, in milliseconds. Deliberately
# generous: it exists to bound a pathological page, not to trade accuracy for
# speed, and a normal filing finishes orders of magnitude inside it. Override
# with TALLIARK_TABLE_BUDGET_MS; zero or negative disables the budget entirely.
DEFAULT_BUDGET_MS = 300_000


def _budget_ms() -> int:
    raw = os.environ.get("TALLIARK_TABLE_BUDGET_MS")
    if raw is None:
        return DEFAULT_BUDGET_MS
    try:
        return int(raw)
    except ValueError:
        return DEFAULT_BUDGET_MS


def detect_tables(
    pdf_bytes: bytes,
    geometry: dict,
    progress_callback=None,
    *,
    diagnostics: dict | None = None,
    periods: bool | None = None,
    budget_ms: int | None = None,
) -> dict:
    geometry_by_page = {int(page["pageIndex"]): page for page in geometry.get("pages", [])}
    budget = _budget_ms() if budget_ms is None else budget_ms
    deadline = time.monotonic() + budget / 1000 if budget > 0 else None
    pages: list[dict] = []
    truncated = False
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page_index in range(doc.page_count):
            # Checked before the page rather than after: the budget is there to
            # stop the next expensive page, and abandoning a page's work after
            # paying for it would buy nothing.
            if deadline is not None and time.monotonic() >= deadline:
                truncated = True
                if progress_callback:
                    progress_callback(
                        f"Table structure detection stopped after page {page_index} "
                        f"of {doc.page_count} at its time budget…",
                        Stage.TABLE_STRUCTURE,
                    )
                break
            if progress_callback:
                progress_callback(
                    f"Detecting table structure page {page_index + 1} of {doc.page_count}…",
                    Stage.TABLE_STRUCTURE,
                    current=page_index + 1,
                    total=doc.page_count,
                    unit="page",
                )
            page_geometry = geometry_by_page.get(
                page_index, {"pageIndex": page_index, "characters": []}
            )
            tables = detect_page(
                page_geometry,
                doc.load_page(page_index),
                page_index=page_index,
                diagnostics=diagnostics,
                periods=periods,
            )
            pages.append({"pageIndex": page_index, "tables": tables})
    finally:
        doc.close()
    if diagnostics is not None:
        diagnostics["table_detector_version"] = DETECTOR_VERSION
        diagnostics["table_period_analysis"] = (
            PERIOD_ANALYSIS if periods is None else bool(periods)
        )
        diagnostics["table_pages_detected"] = len(pages)
        if truncated:
            diagnostics["table_structure_truncated"] = True
    structure = {
        "version": 1,
        "coordinateSpace": "normalized",
        "detectorVersion": DETECTOR_VERSION,
        "pages": pages,
    }
    if truncated:
        structure["truncated"] = True
    return structure


def structure_to_base64(structure: dict) -> str:
    return json_to_base64(structure)
