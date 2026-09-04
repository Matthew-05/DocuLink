import unittest

import pymupdf as fitz

from engines.geometry_engine import (
    _extract_page_characters,
    _extract_page_characters_from_rawdict,
    find_coincident_text_layer_pages,
    find_unstrippable_hidden_text_pages,
)


def _char(rawdict: dict, page_w: float, page_h: float) -> list[str]:
    return [box["char"] for box in _extract_page_characters_from_rawdict(rawdict, page_w, page_h)]


def _text_block(chars: list[tuple[str, tuple[float, float, float, float]]]) -> dict:
    return {
        "type": 0,
        "lines": [
            {
                "spans": [
                    {
                        "chars": [
                            {"c": char, "bbox": list(bbox)}
                            for char, bbox in chars
                        ]
                    }
                ]
            }
        ],
    }


class ExtractPageCharactersFromRawdictTests(unittest.TestCase):
    PAGE_W = 600.0
    PAGE_H = 800.0

    def test_includes_literal_space_between_words(self) -> None:
        rawdict = {
            "blocks": [
                _text_block(
                    [
                        ("h", (10, 10, 20, 30)),
                        ("e", (20, 10, 30, 30)),
                        ("l", (30, 10, 40, 30)),
                        ("l", (40, 10, 50, 30)),
                        ("o", (50, 10, 60, 30)),
                        (" ", (60, 10, 65, 30)),
                        ("w", (70, 10, 80, 30)),
                        ("o", (80, 10, 90, 30)),
                        ("r", (90, 10, 100, 30)),
                        ("l", (100, 10, 110, 30)),
                        ("d", (110, 10, 120, 30)),
                    ]
                )
            ]
        }

        self.assertEqual(_char(rawdict, self.PAGE_W, self.PAGE_H), list("hello world"))

    def test_preserves_multi_line_reading_order(self) -> None:
        rawdict = {
            "blocks": [
                _text_block([("a", (10, 10, 20, 30))]),
                _text_block([("b", (10, 40, 20, 60))]),
            ]
        }

        self.assertEqual(_char(rawdict, self.PAGE_W, self.PAGE_H), ["a", "b"])

        boxes = _extract_page_characters_from_rawdict(rawdict, self.PAGE_W, self.PAGE_H)
        self.assertEqual([box["lineIndex"] for box in boxes], [0, 1])

    def test_removes_a_coincident_duplicate_text_layer(self) -> None:
        first = [
            ("A", (10, 10, 20, 30)),
            ("1", (20, 10, 30, 30)),
        ]
        duplicate = [
            ("A", (10.2, 10.1, 20.2, 30.1)),
            ("1", (20.2, 10.1, 30.2, 30.1)),
        ]
        rawdict = {
            "blocks": [
                _text_block(first),
                _text_block(duplicate),
            ]
        }

        boxes = _extract_page_characters_from_rawdict(
            rawdict, self.PAGE_W, self.PAGE_H
        )

        self.assertEqual([box["char"] for box in boxes], ["A", "1"])
        self.assertEqual([box["lineIndex"] for box in boxes], [0, 0])

    def test_preserves_repeated_text_at_distinct_positions(self) -> None:
        rawdict = {
            "blocks": [
                _text_block([("A", (10, 10, 20, 30))]),
                _text_block([("A", (10, 40, 20, 60))]),
            ]
        }

        boxes = _extract_page_characters_from_rawdict(
            rawdict, self.PAGE_W, self.PAGE_H
        )

        self.assertEqual([box["char"] for box in boxes], ["A", "A"])
        self.assertEqual([box["lineIndex"] for box in boxes], [0, 1])

    def test_preserves_source_line_when_vertical_positions_are_ambiguous(self) -> None:
        rawdict = {
            "blocks": [
                {
                    "type": 0,
                    "lines": [
                        {"spans": [{"chars": [{"c": "7", "bbox": [10, 10, 20, 40]}]}]},
                        {"spans": [{"chars": [{"c": "n", "bbox": [10, 22, 20, 42]}]}]},
                    ],
                }
            ]
        }

        boxes = _extract_page_characters_from_rawdict(rawdict, self.PAGE_W, self.PAGE_H)

        self.assertEqual([box["char"] for box in boxes], ["7", "n"])
        self.assertEqual([box["lineIndex"] for box in boxes], [0, 1])

    def test_skips_non_text_blocks(self) -> None:
        rawdict = {
            "blocks": [
                {"type": 1, "lines": []},
                _text_block([("x", (10, 10, 20, 30))]),
            ]
        }

        self.assertEqual(_char(rawdict, self.PAGE_W, self.PAGE_H), ["x"])

    def test_skips_empty_characters(self) -> None:
        rawdict = {
            "blocks": [
                _text_block(
                    [
                        ("", (10, 10, 20, 30)),
                        ("y", (20, 10, 30, 30)),
                    ]
                )
            ]
        }

        self.assertEqual(_char(rawdict, self.PAGE_W, self.PAGE_H), ["y"])

    def test_normalizes_coordinates(self) -> None:
        rawdict = {"blocks": [_text_block([("z", (60, 80, 120, 160))])]}

        boxes = _extract_page_characters_from_rawdict(rawdict, self.PAGE_W, self.PAGE_H)

        self.assertEqual(len(boxes), 1)
        self.assertEqual(boxes[0]["char"], "z")
        self.assertEqual(boxes[0]["x"], 0.1)
        self.assertEqual(boxes[0]["y"], 0.1)
        self.assertEqual(boxes[0]["width"], 0.1)
        self.assertEqual(boxes[0]["height"], 0.1)
        self.assertEqual(boxes[0]["lineIndex"], 0)

    def test_skips_zero_area_boxes(self) -> None:
        rawdict = {"blocks": [_text_block([("q", (10, 10, 10, 30))])]}

        self.assertEqual(_extract_page_characters_from_rawdict(rawdict, self.PAGE_W, self.PAGE_H), [])

    def test_intrinsic_page_rotation_is_applied_before_normalizing(self) -> None:
        document = fitz.open()
        try:
            page = document.new_page(width=self.PAGE_W, height=self.PAGE_H)
            page.insert_text((60, 80), "Z", fontsize=20)
            page = document.reload_page(page)
            page.set_rotation(90)
            page = document.reload_page(page)

            raw = page.get_text("rawdict")
            raw_character = next(
                character
                for block in raw["blocks"]
                if block["type"] == 0
                for line in block["lines"]
                for span in line["spans"]
                for character in span["chars"]
                if character["c"] == "Z"
            )
            displayed_box = fitz.Rect(raw_character["bbox"]) * page.rotation_matrix

            characters = _extract_page_characters(page)

            self.assertEqual(len(characters), 1)
            character = characters[0]
            self.assertAlmostEqual(character["x"], displayed_box.x0 / page.rect.width)
            self.assertAlmostEqual(character["y"], displayed_box.y0 / page.rect.height)
            self.assertAlmostEqual(
                character["width"], displayed_box.width / page.rect.width
            )
            self.assertAlmostEqual(
                character["height"], displayed_box.height / page.rect.height
            )
            self.assertLessEqual(character["x"] + character["width"], 1.0)
            self.assertLessEqual(character["y"] + character["height"], 1.0)
        finally:
            document.close()

    def test_rotated_page_retains_displayed_reading_order(self) -> None:
        document = fitz.open()
        try:
            page = document.new_page(width=self.PAGE_W, height=self.PAGE_H)
            # After 90 degrees clockwise, A is visually left of B. Sorting in
            # unrotated y coordinates would incorrectly reverse them.
            page.insert_text((60, 700), "A", fontsize=20)
            page.insert_text((60, 100), "B", fontsize=20)
            page = document.reload_page(page)
            page.set_rotation(90)
            page = document.reload_page(page)

            characters = _extract_page_characters(page)

            self.assertEqual([item["char"] for item in characters], ["A", "B"])
            self.assertLess(characters[0]["x"], characters[1]["x"])
        finally:
            document.close()

    def test_coincident_pdf_text_draws_are_extracted_once(self) -> None:
        document = fitz.open()
        try:
            page = document.new_page(width=self.PAGE_W, height=self.PAGE_H)
            page.insert_text((72, 100), "Revenue 123", fontsize=12)
            page.insert_text((72, 100), "Revenue 123", fontsize=12)
            page = document.reload_page(page)

            characters = _extract_page_characters(page)

            self.assertEqual(
                "".join(character["char"] for character in characters),
                "Revenue 123",
            )
        finally:
            document.close()

    def test_detects_differently_encoded_hidden_copies_before_output_is_saved(
        self,
    ) -> None:
        document = fitz.open()
        try:
            page = document.new_page(width=self.PAGE_W, height=self.PAGE_H)
            page.insert_text(
                (72, 100),
                "Revenue 123",
                fontsize=12,
                fill_opacity=0,
            )
            page.insert_text(
                (72.5, 100.2),
                "Revenue I23",
                fontsize=12,
                render_mode=3,
            )

            pages = find_coincident_text_layer_pages(document.tobytes())

            self.assertEqual(pages, [1])
        finally:
            document.close()

    def test_zero_opacity_text_is_forced_instead_of_redo_ocr(self) -> None:
        document = fitz.open()
        try:
            page = document.new_page(width=self.PAGE_W, height=self.PAGE_H)
            page.insert_text(
                (72, 100),
                "Existing OCR",
                fontsize=12,
                fill_opacity=0,
            )

            pages = find_unstrippable_hidden_text_pages(document.tobytes())

            self.assertEqual(pages, [1])
        finally:
            document.close()

    def test_render_mode_three_text_can_still_use_redo_ocr(self) -> None:
        document = fitz.open()
        try:
            page = document.new_page(width=self.PAGE_W, height=self.PAGE_H)
            page.insert_text(
                (72, 100),
                "Existing OCR",
                fontsize=12,
                render_mode=3,
            )

            pages = find_unstrippable_hidden_text_pages(document.tobytes())

            self.assertEqual(pages, [])
        finally:
            document.close()


if __name__ == "__main__":
    unittest.main()
