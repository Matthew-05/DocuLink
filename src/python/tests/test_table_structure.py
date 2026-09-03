import unittest

from PIL import Image, ImageDraw

from engines.table.columns import detect_columns
from engines.table.headers import _cell_text, detect_header
from engines.table.regions import discover_regions, line_records
from engines.table.rows import detect_rows
from engines.table.rulings import _vector_rulings, detect_ruled_grid


def characters(text: str, x: float, y: float, line: int) -> list[dict]:
    return [
        {
            "char": character,
            "x": x + index * 0.012,
            "y": y,
            "width": 0.01,
            "height": 0.025,
            "lineIndex": line,
        }
        for index, character in enumerate(text)
    ]


BOUNDS = {"x": 0.05, "y": 0.05, "width": 0.9, "height": 0.8}
COLUMNS = [{"x0": 0.05, "x1": 0.55}, {"x0": 0.55, "x1": 0.95}]


class RulingTests(unittest.TestCase):
    def test_extracts_synthetic_ruled_grid(self) -> None:
        image = Image.new("L", (500, 300), 255)
        draw = ImageDraw.Draw(image)
        for x in (30, 220, 470):
            draw.line((x, 0, x, 299), fill=0, width=2)
        for y in (20, 100, 180, 280):
            draw.line((0, y, 499, y), fill=0, width=2)
        self.assertEqual(detect_ruled_grid(image), ([30, 220, 470], [20, 100, 180, 280]))

    def test_rotated_page_rulings_are_reported_in_displayed_space(self) -> None:
        class FakeRect:
            x0 = y0 = 0.0
            x1 = width = 600.0
            y1 = height = 400.0

        class FakePage:
            rect = FakeRect()
            # 90-degree rotation: unrotated (x, y) displays at (600 - y, x).
            rotation_matrix = (0.0, 1.0, -1.0, 0.0, 600.0, 0.0)

            def get_drawings(self):
                # Spans the unrotated page horizontally; displays as a vertical rule.
                return [{"type": "s", "color": (0, 0, 0),
                         "items": [("l", (0.0, 100.0), (400.0, 100.0))]}]

        vertical, horizontal = _vector_rulings(FakePage())

        self.assertEqual(horizontal, [])
        self.assertEqual(len(vertical), 1)
        self.assertAlmostEqual(vertical[0], 500.0 / 600.0, places=3)



class RowTests(unittest.TestCase):
    def test_combines_source_lines_on_the_same_visual_row(self) -> None:
        page = {"characters": characters("Label", 0.1, 0.12, 0) + characters("100", 0.7, 0.121, 1)}
        records = line_records(page)
        self.assertEqual(len(records), 1)
        self.assertEqual("".join(item["char"] for item in records[0]["characters"]), "Label100")

    def test_sparse_header_is_not_coalesced_with_lower_data_line(self) -> None:
        page = {"characters": (
            characters("2025", 0.6, 0.10, 0)
            + characters("2024", 0.8, 0.10, 0)
            + characters("Income taxes payable", 0.1, 0.112, 1)
            + characters("13016", 0.6, 0.112, 1)
            + characters("26601", 0.8, 0.112, 1)
        )}

        self.assertEqual(len(line_records(page)), 2)

    def test_rulings_define_rows_absolutely(self) -> None:
        page = {"characters": characters("A", 0.1, 0.12, 0) + characters("1", 0.7, 0.12, 0)}
        rows = detect_rows(page, BOUNDS, [0.05, 0.25, 0.5, 0.85], COLUMNS, grid=True)
        self.assertEqual([(row["y0"], row["y1"]) for row in rows], [(0.05, 0.25), (0.25, 0.5), (0.5, 0.85)])
        self.assertTrue(all(row["mergeConfidence"] == 1.0 for row in rows))

    def test_subtotal_underlines_do_not_define_row_bands(self) -> None:
        # A statement underlines each subtotal. Those rules are emphasis, not
        # structure: letting them band the table dropped the header above them.
        page = {"characters": (
            characters("2025", 0.6, 0.10, 0)
            + characters("Products", 0.1, 0.20, 1)
            + characters("307,003", 0.6, 0.20, 1)
            + characters("Services", 0.1, 0.30, 2)
            + characters("109,158", 0.6, 0.30, 2)
        )}

        rows = detect_rows(page, BOUNDS, [0.26, 0.36, 0.46], COLUMNS)

        self.assertEqual(len(rows), 3)
        self.assertAlmostEqual(rows[0]["y0"], BOUNDS["y"])

    def test_merges_label_only_continuation(self) -> None:
        page = {"characters": (
            characters("Long label", 0.1, 0.12, 0)
            + characters("100", 0.7, 0.12, 0)
            + characters("continued", 0.12, 0.15, 1)
            + characters("Next", 0.1, 0.30, 2)
            + characters("200", 0.7, 0.30, 2)
        )}
        rows = detect_rows(page, BOUNDS, [], COLUMNS)
        self.assertEqual(len(rows), 2)
        self.assertTrue(rows[0]["merged"])
        self.assertEqual(len(rows[0]["textLines"]), 2)

    def test_never_merges_a_line_that_carries_a_value(self) -> None:
        page = {"characters": (
            characters("First", 0.1, 0.12, 0)
            + characters("100", 0.7, 0.12, 0)
            + characters("Second", 0.1, 0.15, 1)
            + characters("200", 0.7, 0.15, 1)
        )}
        rows = detect_rows(page, BOUNDS, [], COLUMNS)
        self.assertEqual(len(rows), 2)
        self.assertFalse(any(row["merged"] for row in rows))


class RegionTests(unittest.TestCase):
    def test_short_whitespace_block_requires_broad_alignment(self) -> None:
        narrow = {"characters": []}
        broad = {"characters": []}
        for line, y in enumerate((0.1, 0.15, 0.2)):
            for x in (0.1, 0.4, 0.7):
                narrow["characters"].extend(characters("X", x, y, line))
            for x in (0.1, 0.32, 0.54, 0.76):
                broad["characters"].extend(characters("X", x, y, line))

        self.assertEqual(discover_regions(narrow, [], []), [])
        self.assertEqual(len(discover_regions(broad, [], [])), 1)

    def test_repeated_hierarchy_outdent_expands_table_left_edge(self) -> None:
        page = {"characters": []}
        for line, y in enumerate((0.1, 0.15, 0.2, 0.25, 0.3, 0.35)):
            label_x = 0.07 if line in (0, 5) else 0.1
            page["characters"].extend(characters("Label", label_x, y, line))
            page["characters"].extend(characters("100", 0.55, y, line))
            page["characters"].extend(characters("200", 0.75, y, line))

        region = discover_regions(page, [], [])[0]

        self.assertLess(region["bounds"]["x"], 0.07)

    def test_single_extreme_glyph_does_not_set_table_left_edge(self) -> None:
        page = {"characters": []}
        for line, y in enumerate((0.1, 0.15, 0.2, 0.25, 0.3, 0.35)):
            if line == 0:
                page["characters"].extend(characters("X", 0.001, y, line))
            page["characters"].extend(characters("Label", 0.1, y, line))
            page["characters"].extend(characters("100", 0.55, y, line))
            page["characters"].extend(characters("200", 0.75, y, line))

        region = discover_regions(page, [], [])[0]

        self.assertGreater(region["bounds"]["x"], 0.04)

    def test_section_caption_is_inside_the_table_bounds(self) -> None:
        # The caption carries one segment so it is not a group member, but it does
        # become a row — the bounds have to reach it or its text is clipped away.
        page = {"characters": []}
        line = 0
        for caption_y, rows in ((0.10, (0.14, 0.18, 0.22)), (0.28, (0.32, 0.36, 0.40))):
            # Outdented by one indent step, as a filing does it (~0.02 of the page).
            page["characters"].extend(characters("Gross margin:", 0.048, caption_y, line))
            line += 1
            for y in rows:
                page["characters"].extend(characters("Products", 0.07, y, line))
                page["characters"].extend(characters("112,887", 0.55, y, line))
                page["characters"].extend(characters("109,633", 0.78, y, line))
                line += 1

        region = discover_regions(page, [], [])[0]

        self.assertLessEqual(region["bounds"]["x"], 0.048)



class HeaderTests(unittest.TestCase):
    def test_collapses_multiline_header_labels(self) -> None:
        page = {"characters": (
            characters("Account", 0.1, 0.10, 0)
            + characters("Current", 0.65, 0.10, 0)
            + characters("name", 0.1, 0.18, 1)
            + characters("year", 0.65, 0.18, 1)
            + characters("Cash", 0.1, 0.32, 2)
            + characters("100", 0.7, 0.32, 2)
        )}
        rows = [
            {"y0": 0.05, "y1": 0.15, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
            {"y0": 0.15, "y1": 0.25, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
            {"y0": 0.25, "y1": 0.45, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
        ]
        header = detect_header(page, COLUMNS, rows)
        self.assertEqual(header, {"rowCount": 2, "labels": ["Account name", "Current year"]})
        self.assertEqual([row["kind"] for row in rows], ["header", "header", "body"])

    def test_ruled_text_table_uses_complete_first_row_as_header(self) -> None:
        page = {"characters": characters("Item", 0.1, 0.10, 0) + characters("Description", 0.65, 0.10, 0)}
        rows = [
            {"y0": 0.05, "y1": 0.15, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 1},
            {"y0": 0.15, "y1": 0.25, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 1},
        ]

        header = detect_header(page, COLUMNS, rows, ruled=True)

        self.assertEqual(header, {"rowCount": 1, "labels": ["Item", "Description"]})
        self.assertEqual(rows[0]["kind"], "header")

    def test_year_header_with_empty_first_cell_stays_one_row(self) -> None:
        page = {"characters": (
            characters("2025", 0.6, 0.10, 0)
            + characters("2024", 0.8, 0.10, 0)
            + characters("Income taxes payable", 0.1, 0.20, 1)
            + characters("$", 0.55, 0.20, 1)
            + characters("13,016", 0.62, 0.20, 1)
            + characters("$", 0.75, 0.20, 1)
            + characters("26,601", 0.82, 0.20, 1)
        )}
        columns = [
            {"x0": 0.05, "x1": 0.45},
            {"x0": 0.45, "x1": 0.7},
            {"x0": 0.7, "x1": 0.95},
        ]
        rows = [
            {"y0": 0.05, "y1": 0.15, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
            {"y0": 0.15, "y1": 0.25, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
        ]

        header = detect_header(page, columns, rows)

        self.assertEqual(header, {"rowCount": 1, "labels": ["", "2025", "2024"]})
        self.assertEqual([row["kind"] for row in rows], ["header", "body"])

    def test_spanning_caption_over_period_band_is_a_two_row_header(self) -> None:
        page = {"characters": (
            characters("Years ended December 31,", 0.55, 0.06, 0)
            + characters("2025", 0.6, 0.10, 1)
            + characters("2024", 0.8, 0.10, 1)
            + characters("Income taxes payable", 0.1, 0.20, 2)
            + characters("13,016", 0.62, 0.20, 2)
            + characters("26,601", 0.82, 0.20, 2)
        )}
        columns = [
            {"x0": 0.05, "x1": 0.45},
            {"x0": 0.45, "x1": 0.7},
            {"x0": 0.7, "x1": 0.95},
        ]
        rows = [
            {"y0": 0.04, "y1": 0.09, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
            {"y0": 0.09, "y1": 0.15, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
            {"y0": 0.15, "y1": 0.25, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
        ]

        header = detect_header(page, columns, rows)

        self.assertEqual(header["rowCount"], 2)
        self.assertEqual([row["kind"] for row in rows], ["header", "header", "body"])

    def test_value_only_row_below_a_header_is_never_promoted(self) -> None:
        page = {"characters": (
            characters("Account", 0.1, 0.10, 0)
            + characters("2025", 0.6, 0.10, 0)
            + characters("Subtotal", 0.1, 0.20, 1)
            + characters("13,016", 0.6, 0.20, 1)
            + characters("2,044", 0.6, 0.30, 2)
        )}
        columns = [{"x0": 0.05, "x1": 0.45}, {"x0": 0.45, "x1": 0.95}]
        rows = [
            {"y0": 0.05, "y1": 0.15, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
            {"y0": 0.15, "y1": 0.25, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
            {"y0": 0.25, "y1": 0.35, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
        ]

        detect_header(page, columns, rows)

        self.assertEqual([row["kind"] for row in rows], ["header", "body", "body"])

    def test_caption_above_the_table_does_not_leak_into_a_label(self) -> None:
        page = {"characters": (
            characters("uncertain tax positions", 0.1, 0.02, 0)
            + characters("Account", 0.1, 0.10, 1)
            + characters("Current", 0.65, 0.10, 1)
            + characters("Cash", 0.1, 0.30, 2)
            + characters("100", 0.7, 0.30, 2)
        )}
        rows = [
            {"y0": 0.0, "y1": 0.15, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
            {"y0": 0.15, "y1": 0.45, "kind": "body", "textLines": [], "merged": False, "mergeConfidence": 0},
        ]

        header = detect_header(page, COLUMNS, rows, bounds={"x": 0.05, "y": 0.08, "width": 0.9, "height": 0.5})

        self.assertEqual(header, {"rowCount": 1, "labels": ["Account", "Current"]})



class ColumnTests(unittest.TestCase):
    def test_accounting_currency_markers_stay_with_amount_columns(self) -> None:
        page = {"characters": []}
        values = [
            ("Income taxes payable", "$", "13,016", "$", "26,601"),
            ("Accrued distribution", "", "8,919", "", "7,679"),
            ("Other liabilities", "", "44,452", "", "44,024"),
            ("Total liabilities", "$", "66,387", "$", "78,304"),
        ]
        positions = (0.08, 0.48, 0.58, 0.72, 0.82)
        for line, row in enumerate(values):
            for text, x in zip(row, positions):
                if text:
                    page["characters"].extend(characters(text, x, 0.1 + line * 0.05, line))

        columns = detect_columns(page, {"x": 0.05, "y": 0.05, "width": 0.9, "height": 0.3}, [])

        self.assertEqual(len(columns), 3)

    def test_offset_period_header_does_not_move_column_boundaries(self) -> None:
        def build(with_header: bool) -> dict:
            page = {"characters": []}
            line = 0
            y = 0.10
            if with_header:
                for label, x in zip(("2025", "2024"), (0.6, 0.8)):
                    page["characters"].extend(characters(label, x, y, line))
                line += 1
                y += 0.06
            for label, first, second in (
                ("Beginning", "22,038", "19,454"),
                ("Increases", "1,971", "1,727"),
                ("Decreases", "(71)", "(386)"),
                ("Ending", "23,242", "22,038"),
            ):
                page["characters"].extend(characters(label, 0.1, y, line))
                page["characters"].extend(characters(first, 0.6, y, line))
                page["characters"].extend(characters(second, 0.8, y, line))
                line += 1
                y += 0.06
            return page

        bounds = {"x": 0.05, "y": 0.05, "width": 0.9, "height": 0.5}
        with_header = detect_columns(build(True), bounds, [])
        without_header = detect_columns(build(False), bounds, [])

        self.assertEqual(len(with_header), len(without_header))
        for first, second in zip(with_header, without_header):
            self.assertAlmostEqual(first["x1"], second["x1"], delta=0.01)

    def test_band_empty_in_every_row_is_collapsed(self) -> None:
        page = {"characters": []}
        for line, y in enumerate((0.1, 0.16, 0.22, 0.28)):
            page["characters"].extend(characters("Label", 0.08, y, line))
            page["characters"].extend(characters("100", 0.40, y, line))
            page["characters"].extend(characters("200", 0.85, y, line))

        columns = detect_columns(page, {"x": 0.05, "y": 0.05, "width": 0.9, "height": 0.35}, [])

        self.assertEqual(len(columns), 3)

    def test_band_only_the_header_fills_is_collapsed(self) -> None:
        # A wide corridor's centre can land inside a header label when the column
        # below it is empty. Splitting there yields a column no row populates.
        page = {"characters": []}
        page["characters"].extend(characters("Ref", 0.08, 0.10, 0))
        page["characters"].extend(characters("P. O. number", 0.40, 0.10, 0))
        page["characters"].extend(characters("Total", 0.85, 0.10, 0))
        for line, y in enumerate((0.18, 0.26, 0.34), start=1):
            page["characters"].extend(characters("INV-100", 0.08, y, line))
            page["characters"].extend(characters("250.00", 0.85, y, line))

        columns = detect_columns(page, {"x": 0.05, "y": 0.05, "width": 0.9, "height": 0.4}, [])

        labels = [_cell_text(page, column, [{"y0": 0.05, "y1": 0.14}]) for column in columns]
        self.assertNotIn("P.", labels)
        self.assertTrue(any("number" in label for label in labels))


if __name__ == "__main__":
    unittest.main()
