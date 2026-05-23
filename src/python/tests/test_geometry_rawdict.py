import unittest

from engines.geometry_engine import _extract_page_characters_from_rawdict


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

    def test_skips_zero_area_boxes(self) -> None:
        rawdict = {"blocks": [_text_block([("q", (10, 10, 10, 30))])]}

        self.assertEqual(_extract_page_characters_from_rawdict(rawdict, self.PAGE_W, self.PAGE_H), [])


if __name__ == "__main__":
    unittest.main()
