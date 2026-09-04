"""Shared corpus plumbing for the table diagnostic scripts.

Both the scorer and the overlay renderer need the same two things: text geometry
with OCR filled in for image-only pages, and a way to run either detector. Kept
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

from engines.geometry_engine import extract_text_geometry  # noqa: E402
from engines.table.detector import DEFAULT_DETECTOR, DETECTORS, detect_tables  # noqa: E402


# A page with fewer meaningful characters than this has no usable text layer.
_SPARSE_PAGE_CHARACTERS = 20


def geometry_for(pdf_bytes: bytes) -> dict:
    """Text geometry, with direct OCR substituted on pages that have no text."""
    geometry = extract_text_geometry(pdf_bytes)
    sparse_pages = [
        page["pageIndex"] + 1
        for page in geometry["pages"]
        if sum(bool(str(char.get("char", "")).strip()) for char in page["characters"])
        < _SPARSE_PAGE_CHARACTERS
    ]
    if sparse_pages:
        # Imported lazily: the OCR runtime pulls in Tesseract and Ghostscript, and
        # a born-digital corpus should not pay for them.
        from engines.ocr_engine import extract_direct_text_geometry

        ocr_pages, _ = extract_direct_text_geometry(pdf_bytes, sparse_pages, dpi=300, psm=6)
        replacements = {page_number - 1: page for page_number, page in ocr_pages.items()}
        geometry["pages"] = [
            replacements.get(page["pageIndex"], page) for page in geometry["pages"]
        ]
    return geometry


def detect(
    pdf_bytes: bytes,
    geometry: dict,
    *,
    detector: str = DEFAULT_DETECTOR,
    periods: bool | None = None,
) -> tuple[dict, dict, float]:
    """Run one detector, returning its structure, diagnostics and elapsed seconds."""
    diagnostics: dict = {}
    started = time.perf_counter()
    structure = detect_tables(
        pdf_bytes, geometry, detector=detector, diagnostics=diagnostics, periods=periods
    )
    return structure, diagnostics, time.perf_counter() - started


__all__ = ["DEFAULT_DETECTOR", "DETECTORS", "PYTHON_ROOT", "REPO_ROOT", "detect", "geometry_for"]
