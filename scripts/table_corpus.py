"""Shared corpus plumbing for the table diagnostic scripts.

Both the scorer and the overlay renderer need the same two things: text geometry
with OCR filled in for image-only pages, and a way to run the detector. Kept
in one place so the two tools can never drift into measuring different inputs.
"""
from __future__ import annotations

import sys
import time
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
PYTHON_ROOT = REPO_ROOT / "src" / "python"
if str(PYTHON_ROOT) not in sys.path:
    sys.path.insert(0, str(PYTHON_ROOT))

from engines.geometry_engine import (  # noqa: E402
    extract_text_geometry,
    find_coincident_text_layer_pages,
    find_unstrippable_hidden_text_pages,
)
from engines.ocr_engine import (  # noqa: E402
    extract_direct_text_geometry,
    merge_geometry_pages,
    select_pages_requiring_ocr,
    summarize_geometry_quality,
)
from engines.table.detector import detect_tables  # noqa: E402
from engines.table_cell_engine import recover_table_geometry  # noqa: E402


def geometry_for(pdf_bytes: bytes) -> dict:
    """Build the same geometry-first input that production table detection uses."""
    geometry = extract_text_geometry(pdf_bytes)
    summary = summarize_geometry_quality(geometry)
    selected = set(select_pages_requiring_ocr(pdf_bytes, summary))
    selected.update(find_unstrippable_hidden_text_pages(pdf_bytes))
    selected.update(find_coincident_text_layer_pages(pdf_bytes))
    if selected:
        ocr_pages, _ = extract_direct_text_geometry(
            pdf_bytes,
            sorted(selected),
            dpi=300,
            psm=3,
        )
        geometry = merge_geometry_pages(geometry, ocr_pages)
    recovered, stats = recover_table_geometry(pdf_bytes, geometry)
    return recovered if stats["changed"] else geometry


def detect(
    pdf_bytes: bytes,
    geometry: dict,
    *,
    periods: bool | None = None,
) -> tuple[dict, dict, float]:
    """Run the detector, returning its structure, diagnostics and elapsed seconds.

    The diagnostic tools measure detection itself, so the wall-clock budget the
    worker applies is disabled here: a slow page is a result to look at, not one
    to truncate.
    """
    diagnostics: dict = {}
    started = time.perf_counter()
    structure = detect_tables(
        pdf_bytes, geometry, diagnostics=diagnostics, periods=periods, budget_ms=0
    )
    return structure, diagnostics, time.perf_counter() - started


__all__ = ["PYTHON_ROOT", "REPO_ROOT", "detect", "geometry_for"]
