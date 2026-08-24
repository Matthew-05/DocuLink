import io
import unittest
from unittest.mock import patch

import pymupdf as fitz
from PIL import Image
from schemas.models import OcrJob

from engines.ocr_engine import (
    PROFILE_DEFAULT,
    PROFILE_HIGH_RESOLUTION_AUTO,
    PROFILE_TABLE_SINGLE_BLOCK,
    PROFILE_TABLE_SPARSE,
    _geometry_page_from_hocr,
    merge_geometry_pages,
    needs_adaptive_retry,
    needs_garbled_text_retry,
    needs_high_resolution_retry,
    needs_low_resolution_quality_retry,
    ocr_pdf_bytes,
    profile_ocr_options,
    select_pages_requiring_ocr,
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


def _geometry_pages(*page_lines: tuple[str, ...]) -> dict:
    pages = []
    for page_index, lines in enumerate(page_lines):
        characters = []
        for line_index, text in enumerate(lines):
            for char in text:
                characters.append({"char": char, "lineIndex": line_index})
        pages.append({"pageIndex": page_index, "characters": characters})
    return {
        "version": 1,
        "coordinateSpace": "normalized",
        "pages": pages,
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

    def test_dense_punctuation_garbage_requests_forced_ocr(self) -> None:
        summary = summarize_geometry_quality(_geometry("* $ # ? H " * 30))

        self.assertEqual(summary["non_whitespace_characters"], 150)
        self.assertEqual(summary["alphanumeric_ratio"], 0.2)
        self.assertTrue(needs_garbled_text_retry(summary))

    def test_normal_transaction_text_is_not_garbled(self) -> None:
        summary = summarize_geometry_quality(
            _geometry(
                "Nov 03 DESKTOP REMOTE DEPOSIT REF 81531242 194,705.05 " * 5
            )
        )

        self.assertGreater(summary["alphanumeric_ratio"], 0.8)
        self.assertFalse(needs_garbled_text_retry(summary))

    def test_healthy_pages_cannot_hide_one_garbled_page(self) -> None:
        summary = summarize_geometry_quality(
            _geometry_pages(
                tuple(
                    "Nov 03 DESKTOP REMOTE DEPOSIT REF 81531242 194,705.05"
                    for _ in range(30)
                ),
                tuple("* $ # ? H " * 10 for _ in range(8)),
            )
        )

        self.assertGreater(summary["alphanumeric_ratio"], 0.70)
        self.assertEqual(summary["garbled_page_numbers"], [2])
        self.assertTrue(needs_garbled_text_retry(summary))

    def test_invalid_unicode_page_requests_forced_ocr(self) -> None:
        summary = summarize_geometry_quality(
            _geometry("Account activity " + "\ufffd\ue000" * 50)
        )

        self.assertEqual(summary["garbled_page_numbers"], [1])
        self.assertTrue(needs_garbled_text_retry(summary))

    def test_short_symbol_sample_does_not_force_ocr(self) -> None:
        summary = summarize_geometry_quality(_geometry("* $ # ?"))

        self.assertFalse(needs_garbled_text_retry(summary))


class DirectGeometryTests(unittest.TestCase):
    def test_hocr_character_boxes_preserve_text_spaces_and_coordinates(self) -> None:
        hocr = b"""<?xml version='1.0' encoding='UTF-8'?>
        <html xmlns='http://www.w3.org/1999/xhtml'><body>
          <span class='ocr_line' title='bbox 10 20 100 40'>
            <span class='ocrx_word' title='bbox 10 20 40 40; x_wconf 91'>
              <span class='ocrx_cinfo' title='x_bboxes 10 20 20 40'>H</span>
              <span class='ocrx_cinfo' title='x_bboxes 20 20 30 40'>i</span>
            </span>
            <span class='ocrx_word' title='bbox 50 20 80 40; x_wconf 95'>
              <span class='ocrx_cinfo' title='x_bboxes 50 20 60 40'>4</span>
              <span class='ocrx_cinfo' title='x_bboxes 60 20 70 40'>2</span>
            </span>
          </span>
          <span class='ocr_header' title='bbox 10 60 40 80'>
            <span class='ocrx_word' title='bbox 10 60 30 80; x_wconf 88'>
              <span class='ocrx_cinfo' title='x_bboxes 10 80 20 80'>O</span>
              <span class='ocrx_cinfo' title='x_bboxes 20 80 30 80'>K</span>
            </span>
          </span>
        </body></html>"""

        page, stats = _geometry_page_from_hocr(hocr, 2, (100, 200))

        self.assertEqual("".join(c["char"] for c in page["characters"]), "Hi 42OK")
        self.assertEqual(page["pageIndex"], 2)
        self.assertAlmostEqual(page["characters"][0]["x"], 0.1)
        self.assertAlmostEqual(page["characters"][0]["y"], 0.1)
        self.assertEqual(stats["word_count"], 3)
        self.assertEqual(stats["character_count"], 6)
        self.assertEqual(stats["mean_confidence"], 91.33)

    def test_merge_replaces_only_selected_pages(self) -> None:
        source = _geometry_pages(("native one",), ("native two",))
        replacement = _geometry("ocr two")["pages"][0]
        replacement["pageIndex"] = 1

        merged = merge_geometry_pages(source, {2: replacement})

        self.assertEqual(
            "".join(c["char"] for c in merged["pages"][0]["characters"]),
            "native one",
        )
        self.assertEqual(
            "".join(c["char"] for c in merged["pages"][1]["characters"]),
            "ocr two",
        )

    def test_dense_native_page_is_not_selected_for_ocr(self) -> None:
        doc = fitz.open()
        try:
            page = doc.new_page(width=600, height=800)
            page.insert_text(
                (40, 60),
                "Transaction description reference 123456 amount 1,234.56 " * 5,
            )
            pdf_bytes = doc.tobytes()
        finally:
            doc.close()
        summary = summarize_geometry_quality(
            _geometry("Transaction description reference 123456 amount 1,234.56 " * 5)
        )

        self.assertEqual(select_pages_requiring_ocr(pdf_bytes, summary), [])

    def test_substantial_image_without_text_is_selected_for_ocr(self) -> None:
        pdf_bytes = _pdf_with_image((800, 800), fitz.Rect(50, 100, 550, 600))
        summary = summarize_geometry_quality(
            {
                "version": 1,
                "coordinateSpace": "normalized",
                "pages": [{"pageIndex": 0, "characters": []}],
            }
        )

        self.assertEqual(select_pages_requiring_ocr(pdf_bytes, summary), [1])


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

    def test_dense_text_does_not_retry_only_because_an_image_is_low_dpi(self) -> None:
        summary = summarize_geometry_quality(
            _geometry_pages(
                *[
                    tuple(
                        "Transaction description reference 123456 amount 1,234.56"
                        for _ in range(20)
                    )
                    for _ in range(6)
                ]
            )
        )

        self.assertFalse(needs_low_resolution_quality_retry(summary, True))

    def test_weak_text_on_low_dpi_scan_requests_retry(self) -> None:
        summary = summarize_geometry_quality(
            _geometry("Invoice total 123.45", "Account 5678")
        )

        self.assertTrue(needs_low_resolution_quality_retry(summary, True))
        self.assertFalse(needs_low_resolution_quality_retry(summary, False))


class ProtocolModelTests(unittest.TestCase):
    def test_ocr_job_reads_source_preservation_flag(self) -> None:
        job = OcrJob.from_dict(
            {
                "job_id": "job-1",
                "command": "ocr",
                "pdf_base64": "JVBERg==",
                "mode": "full",
                "preserve_source_pdf": True,
            }
        )

        self.assertTrue(job.preserve_source_pdf)


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

    @patch("engines.ocr_engine.ocrmypdf.ocr")
    def test_ocr_pdf_forwards_selected_pages(self, mock_ocr) -> None:
        def write_output(_source, destination, **_options):
            with open(destination, "wb") as stream:
                stream.write(b"ocr-result")

        mock_ocr.side_effect = write_output

        result = ocr_pdf_bytes(b"source-pdf", mode="force", pages="2,5-7")

        self.assertEqual(result, b"ocr-result")
        self.assertEqual(mock_ocr.call_args.kwargs["pages"], "2,5-7")

    @patch("engines.ocr_engine.ocrmypdf.ocr")
    def test_ocr_pdf_omits_rotation_threshold_when_rotation_disabled(
        self,
        mock_ocr,
    ) -> None:
        def write_output(_source, destination, **_options):
            with open(destination, "wb") as stream:
                stream.write(b"ocr-result")

        mock_ocr.side_effect = write_output

        ocr_pdf_bytes(b"source-pdf", auto_rotate_pages=False)

        self.assertFalse(mock_ocr.call_args.kwargs["rotate_pages"])
        self.assertNotIn("rotate_pages_threshold", mock_ocr.call_args.kwargs)


if __name__ == "__main__":
    unittest.main()
