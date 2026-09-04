"""Document-level detection behaviour: what the model says about itself.

The per-page detection rules are covered in test_table_pipeline.py. What matters
here is the envelope the worker stores: which detector built it, and whether it
is complete.
"""
from __future__ import annotations

import unittest
from pathlib import Path

import pymupdf

from engines.geometry_engine import extract_text_geometry
from engines.table.detector import detect_tables
from engines.table.redesign import DETECTOR_VERSION


FIXTURES = Path(__file__).resolve().parent / "fixtures" / "tables" / "synthetic"


def _document(page_count: int) -> bytes:
    """One fixture page repeated, so every page holds exactly one table."""
    source = pymupdf.open(str(FIXTURES / "ruled-grid.pdf"))
    document = pymupdf.open()
    try:
        for _ in range(page_count):
            document.insert_pdf(source)
        return document.tobytes()
    finally:
        document.close()
        source.close()


class ModelEnvelopeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.pdf = _document(4)
        self.geometry = extract_text_geometry(self.pdf)

    def test_the_model_records_which_detector_built_it(self) -> None:
        diagnostics: dict = {}
        structure = detect_tables(self.pdf, self.geometry, diagnostics=diagnostics, budget_ms=0)

        # Stored with the model, not only in diagnostics: a cached structure
        # outlives the job that produced it, so it has to carry its own version.
        self.assertEqual(structure["detectorVersion"], DETECTOR_VERSION)
        self.assertEqual(diagnostics["table_detector_version"], DETECTOR_VERSION)
        self.assertEqual(len(structure["pages"]), 4)
        self.assertNotIn("truncated", structure)

    def test_a_spent_budget_stops_detection_and_says_so(self) -> None:
        diagnostics: dict = {}
        structure = detect_tables(self.pdf, self.geometry, diagnostics=diagnostics, budget_ms=1)

        # The pages that were detected are kept and correct; the rest are absent
        # rather than present and empty, so nobody reads "no tables here" from a
        # page that was never examined.
        self.assertTrue(structure["truncated"])
        self.assertTrue(diagnostics["table_structure_truncated"])
        self.assertLess(len(structure["pages"]), 4)
        self.assertEqual(diagnostics["table_pages_detected"], len(structure["pages"]))
        for page in structure["pages"]:
            self.assertEqual(len(page["tables"]), 1)

    def test_the_budget_can_be_switched_off_for_diagnostics(self) -> None:
        structure = detect_tables(self.pdf, self.geometry, diagnostics={}, budget_ms=0)

        self.assertEqual(len(structure["pages"]), 4)


if __name__ == "__main__":
    unittest.main()
