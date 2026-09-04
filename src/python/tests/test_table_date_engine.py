import unittest
from unittest.mock import patch

from PIL import Image, ImageDraw

from engines.table_date_engine import (
    _recognize_date,
    normalize_date,
)
from engines.table.rulings import detect_ruled_grid, detect_ruled_grid_segments


class DateNormalizationTests(unittest.TestCase):
    def test_normalizes_eight_digits(self) -> None:
        self.assertEqual(normalize_date("06302016"), "06/30/2016")

    def test_rejects_spurious_leading_digit(self) -> None:
        self.assertIsNone(normalize_date("108/31/2016"))

    def test_accepts_single_digit_month_and_day(self) -> None:
        self.assertEqual(normalize_date("9/3/2019"), "9/3/2019")

    def test_rejects_impossible_calendar_date(self) -> None:
        self.assertIsNone(normalize_date("02/30/2016"))

    def test_rejects_implausible_year(self) -> None:
        self.assertIsNone(normalize_date("06/30/2201"))


class RuledGridDetectionTests(unittest.TestCase):
    def test_detected_grid_segments_share_the_grid_envelope(self) -> None:
        image = Image.new("L", (500, 300), 255)
        draw = ImageDraw.Draw(image)
        for x in (40, 140, 260, 460):
            draw.line((x, 30, x, 270), fill=0, width=2)
        for y in (30, 90, 170, 270):
            draw.line((40, y, 460, y), fill=0, width=2)

        segments = detect_ruled_grid_segments(image)
        vertical = [item for item in segments if item.axis == "vertical"]
        horizontal = [item for item in segments if item.axis == "horizontal"]

        self.assertEqual(len(vertical), 4)
        self.assertEqual(len(horizontal), 4)
        self.assertTrue(all(item.start == vertical[0].start for item in vertical))
        self.assertTrue(all(item.end == vertical[0].end for item in vertical))
        self.assertTrue(all(item.start == horizontal[0].start for item in horizontal))
        self.assertTrue(all(item.end == horizontal[0].end for item in horizontal))

    def test_detects_and_collapses_two_pixel_grid_lines(self) -> None:
        image = Image.new("L", (500, 300), 255)
        draw = ImageDraw.Draw(image)
        for x in (40, 140, 260, 460):
            draw.line((x, 0, x, 299), fill=0, width=2)
        for y in (30, 90, 170, 270):
            draw.line((0, y, 499, y), fill=0, width=2)

        x_lines, y_lines = detect_ruled_grid(image)

        self.assertEqual(x_lines, [40, 140, 260, 460])
        self.assertEqual(y_lines, [30, 90, 170, 270])

    def test_detects_faint_gray_grid_and_merges_nearby_annotation(self) -> None:
        image = Image.new("L", (500, 300), 255)
        draw = ImageDraw.Draw(image)
        for x in (40, 140, 260, 460):
            draw.line((x, 0, x, 299), fill=175, width=2)
        draw.line((264, 0, 264, 260), fill=40, width=1)
        for y in (30, 90, 170, 270):
            draw.line((0, y, 499, y), fill=175, width=2)

        x_lines, y_lines = detect_ruled_grid(image)

        self.assertEqual(len(x_lines), 4)
        self.assertTrue(260 <= x_lines[2] <= 264)
        self.assertEqual(y_lines, [30, 90, 170, 270])

    def test_ignores_small_non_table_images(self) -> None:
        image = Image.new("L", (200, 100), 255)

        self.assertEqual(detect_ruled_grid(image), ([], []))


class CellRetryTests(unittest.TestCase):
    @patch("engines.table_date_engine._ocr_text")
    def test_uses_variant_consensus_when_valid_readings_disagree(
        self,
        mock_ocr_text,
    ) -> None:
        mock_ocr_text.side_effect = [
            "08/31/2018",
            "08/31/2016",
            "08/31/2016",
        ]

        result = _recognize_date(Image.new("L", (60, 12), 255), "eng")

        self.assertEqual(result, "08/31/2016")
        self.assertEqual(mock_ocr_text.call_count, 3)
        self.assertEqual(mock_ocr_text.call_args_list[2].kwargs["psm"], 13)


if __name__ == "__main__":
    unittest.main()
