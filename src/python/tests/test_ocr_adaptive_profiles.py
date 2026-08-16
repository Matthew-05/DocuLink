import io
import unittest
from unittest.mock import patch

import pymupdf as fitz
from PIL import Image

from engines.ocr_engine import (
    PROFILE_DEFAULT,
    PROFILE_HIGH_RESOLUTION_AUTO,
    PROFILE_TABLE_SINGLE_BLOCK,
    PROFILE_TABLE_SPARSE,
    needs_adaptive_retry,
    needs_high_resolution_retry,
    ocr_pdf_bytes,
    profile_ocr_options,
    select_best_adaptive_profile,
    summarize_geometry_quality,
)


def _geometry(*lines: str) -> dict:
    characters = []
    for line_index, text in enumerate(lines):
        for char in text:
            characters.append({"char": char, "lineIndex": line_index})
    return {
        "version": 1,
        "coordinateSpace": "normalized",
        "pages": [{"pageIndex": 0, "characters": characters}],
    }


def _pdf_with_image(
    image_size: tuple[int, int],
    display_rect: fitz.Rect,
) -> bytes:
    image = Image.new("RGB", image_size, "white")
    image_stream = io.BytesIO()
    image.save(image_stream, format="PNG")
    doc = fitz.open()
    try:
        page = doc.new_page(width=600, height=800)
        page.insert_image(display_rect, stream=image_stream.getvalue())
        return doc.tobytes()
    finally:
        doc.close()


class GeometryQualityTests(unittest.TestCase):
    def test_catastrophically_sparse_text_requests_retry(self) -> None:
        summary = summarize_geometry_quality(
            _geometry("Fac Sani", "DCS Facity Sevices", "Fac Sarvs")
        )

        self.assertEqual(summary["page_count"], 1)
        self.assertEqual(summary["word_count"], 7)
        self.assertTrue(needs_adaptive_retry(summary))

    def test_dense_multi_line_text_keeps_default_profile(self) -> None:
        summary = summarize_geometry_quality(
            _geometry(
                *[
                    "06/30/2016 DCS Facility Services Closure Permanent"
                    for _ in range(12)
                ]
            )
        )

        self.assertGreater(summary["word_count"], 50)
        self.assertFalse(needs_adaptive_retry(summary))


class ScanResolutionTests(unittest.TestCase):
    def test_material_low_resolution_scan_requests_retry(self) -> None:
        pdf_bytes = _pdf_with_image((500, 500), fitz.Rect(100, 150, 500, 550))

        self.assertTrue(needs_high_resolution_retry(pdf_bytes))

    def test_high_resolution_scan_keeps_normal_path(self) -> None:
        pdf_bytes = _pdf_with_image((2000, 2000), fitz.Rect(100, 150, 500, 550))

        self.assertFalse(needs_high_resolution_retry(pdf_bytes))

    def test_small_low_resolution_logo_does_not_request_retry(self) -> None:
        pdf_bytes = _pdf_with_image((50, 50), fitz.Rect(10, 10, 60, 60))

        self.assertFalse(needs_high_resolution_retry(pdf_bytes))

    def test_empty_document_does_not_retry(self) -> None:
        summary = summarize_geometry_quality({"pages": []})

        self.assertFalse(needs_adaptive_retry(summary))


class AdaptiveProfileSelectionTests(unittest.TestCase):
    def test_single_block_wins_close_table_evaluation_for_reading_order(self) -> None:
        chosen = select_best_adaptive_profile(
            [
                {
                    "profile": PROFILE_TABLE_SINGLE_BLOCK,
                    "mean_confidence": 32.2,
                    "word_count": 148,
                },
                {
                    "profile": PROFILE_TABLE_SPARSE,
                    "mean_confidence": 28.9,
                    "word_count": 238,
                },
            ]
        )

        self.assertEqual(chosen["profile"], PROFILE_TABLE_SINGLE_BLOCK)

    def test_sparse_profile_can_win_with_materially_better_confidence(self) -> None:
        chosen = select_best_adaptive_profile(
            [
                {
                    "profile": PROFILE_TABLE_SINGLE_BLOCK,
                    "mean_confidence": 30.0,
                    "word_count": 100,
                },
                {
                    "profile": PROFILE_TABLE_SPARSE,
                    "mean_confidence": 48.0,
                    "word_count": 120,
                },
            ]
        )

        self.assertEqual(chosen["profile"], PROFILE_TABLE_SPARSE)

    def test_profile_options_are_explicit(self) -> None:
        self.assertEqual(
            profile_ocr_options(PROFILE_DEFAULT),
            {"tesseract_pagesegmode": None, "oversample": 0},
        )
        self.assertEqual(
            profile_ocr_options(PROFILE_HIGH_RESOLUTION_AUTO),
            {"tesseract_pagesegmode": 3, "oversample": 300},
        )
        self.assertEqual(
            profile_ocr_options(PROFILE_TABLE_SINGLE_BLOCK),
            {"tesseract_pagesegmode": 6, "oversample": 300},
        )
        self.assertEqual(
            profile_ocr_options(PROFILE_TABLE_SPARSE),
            {"tesseract_pagesegmode": 11, "oversample": 300},
        )

    @patch("engines.ocr_engine.ocrmypdf.ocr")
    def test_ocr_pdf_forwards_adaptive_options(self, mock_ocr) -> None:
        def write_output(_source, destination, **_options):
            with open(destination, "wb") as stream:
                stream.write(b"ocr-result")

        mock_ocr.side_effect = write_output

        result = ocr_pdf_bytes(
            b"source-pdf",
            tesseract_pagesegmode=6,
            oversample=300,
        )

        self.assertEqual(result, b"ocr-result")
        self.assertEqual(mock_ocr.call_args.kwargs["tesseract_pagesegmode"], 6)
        self.assertEqual(mock_ocr.call_args.kwargs["oversample"], 300)


if __name__ == "__main__":
    unittest.main()
