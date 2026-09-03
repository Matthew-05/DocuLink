from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path

import pymupdf


REPO_ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO_ROOT))

from scripts.render_table_overlay import (  # noqa: E402
    _geometry_with_sparse_page_ocr,
    render_overlay,
)
from engines.table.detector import detect_tables  # noqa: E402


class TableOverlayTests(unittest.TestCase):
    def test_render_overlay_writes_one_page_per_detected_table_page(self) -> None:
        source_value = os.environ.get("DOCULINK_OVERLAY_PDF")
        if not source_value:
            self.skipTest("DOCULINK_OVERLAY_PDF is not set")

        source = Path(source_value)
        pdf_bytes = source.read_bytes()
        geometry = _geometry_with_sparse_page_ocr(pdf_bytes)
        model = detect_tables(pdf_bytes, geometry)
        expected_page_count = sum(bool(page["tables"]) for page in model["pages"])
        if expected_page_count == 0:
            self.fail("DOCULINK_OVERLAY_PDF contains no detected tables")

        with tempfile.TemporaryDirectory() as temporary_directory:
            output = Path(temporary_directory) / "table-overlay.pdf"
            result = render_overlay(source, output)

            self.assertEqual(output, result)
            self.assertTrue(output.exists())
            rendered = pymupdf.open(output)
            try:
                self.assertEqual(expected_page_count, rendered.page_count)
            finally:
                rendered.close()


if __name__ == "__main__":
    unittest.main()
