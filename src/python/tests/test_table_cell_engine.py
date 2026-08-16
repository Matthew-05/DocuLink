import unittest

from PIL import Image, ImageDraw

from engines.table_cell_engine import (
    _canonicalize_repeated_cells,
    _cleanup_text,
    _cleanup_sparse_word,
    _header_line_texts,
    _ink_box,
    _ink_line_boxes,
    _select_city,
    _select_number,
    _select_status,
)


class TextCleanupTests(unittest.TestCase):
    def test_repairs_common_business_suffix_artifacts(self) -> None:
        self.assertEqual(
            _cleanup_text("Peppermint Holdings, LLC d/ob/aOne"),
            "Peppermint Holdings, LLC d/b/a One",
        )

    def test_repairs_initial_i_and_burg_suffix(self) -> None:
        self.assertEqual(_cleanup_text("O.P.1. Products Inc."), "O.P.I. Products Inc.")
        self.assertEqual(_cleanup_text("Pittsbura"), "Pittsburg")
        self.assertEqual(_cleanup_text("Vacaville -—"), "Vacaville")

    def test_normalizes_degraded_ordinal_tokens(self) -> None:
        self.assertEqual(_cleanup_sparse_word("10�"), "10th")
        self.assertEqual(_cleanup_sparse_word("25�"), "25th")
        self.assertEqual(_cleanup_sparse_word("—"), "-")
        self.assertIsNone(_cleanup_sparse_word("~~"))


class InkGeometryTests(unittest.TestCase):
    def test_detects_tight_single_line_ink_box(self) -> None:
        image = Image.new("L", (30, 12), 255)
        draw = ImageDraw.Draw(image)
        draw.rectangle((4, 3, 20, 8), fill=0)

        self.assertEqual(_ink_line_boxes(image), [(4, 3, 21, 9)])
        self.assertEqual(_ink_box(image), (4, 3, 21, 9))

    def test_preserves_known_multiline_header_layout(self) -> None:
        image = Image.new("L", (40, 20), 255)
        draw = ImageDraw.Draw(image)
        draw.rectangle((3, 2, 30, 7), fill=0)
        draw.rectangle((10, 12, 25, 17), fill=0)

        self.assertEqual(
            _ink_line_boxes(image),
            [(3, 2, 31, 8), (10, 12, 26, 18)],
        )
        self.assertEqual(
            _header_line_texts("Effective Date", 2),
            ["Effective", "Date"],
        )


class ColumnSelectionTests(unittest.TestCase):
    def test_number_prefers_stronger_evidence_over_repeated_low_confidence_error(self) -> None:
        candidates = [
            {"source": "raw-threshold", "text": "643", "confidence": 67.0},
            {"source": "clean", "text": "543", "confidence": 65.0},
            {"source": "clean-small", "text": "543", "confidence": 62.0},
        ]

        self.assertEqual(_select_number(candidates), "643")

    def test_city_consensus_uses_raw_result_as_tie_break(self) -> None:
        candidates = [
            {"source": "raw", "text": "Antelope", "confidence": 61.0},
            {"source": "raw-small", "text": "Antelope", "confidence": 71.0},
            {"source": "clean", "text": "Antelooe", "confidence": 75.0},
            {"source": "clean-small", "text": "Antelooe", "confidence": 86.0},
        ]

        self.assertEqual(_select_city(candidates), "Antelope")

    def test_status_maps_noisy_ocr_to_controlled_phrase(self) -> None:
        candidates = [
            {"source": "raw", "text": "Lavoff Temoorarv", "confidence": 90.0}
        ]

        self.assertEqual(_select_status(candidates), "Layoff Temporary")


class RepeatedValueTests(unittest.TestCase):
    def test_gridless_consensus_repairs_repeated_company_name(self) -> None:
        cells = [
            {
                "text": "OCS Facility Services",
                "candidates": [
                    {"source": "raw", "text": "OCS Facility Services"},
                    {"source": "clean", "text": "DCS Facility Services"},
                ],
            }
            for _ in range(5)
        ]
        cells.append(
            {
                "text": "DCS Facility Services",
                "candidates": [
                    {"source": "raw", "text": "DCS Facility Services"},
                    {"source": "clean", "text": "DCS Facility Services"},
                ],
            }
        )

        _canonicalize_repeated_cells(cells)

        self.assertEqual(
            {cell["text"] for cell in cells},
            {"DCS Facility Services"},
        )


if __name__ == "__main__":
    unittest.main()
