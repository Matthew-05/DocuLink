"""Run the committed synthetic table fixtures through the scorer.

Each fixture reproduces one failure mode the Apple filing exposed. The goldens
are derived from the drawn layout by scripts/make_table_fixtures.py, never from
detector output, so this is a real regression gate rather than a snapshot.
"""
from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPTS = REPO_ROOT / "scripts"
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))

FIXTURES = Path(__file__).resolve().parent / "fixtures" / "tables" / "synthetic"

from score_tables import _boundary_counts, _positions, match_tables  # noqa: E402
from table_corpus import detect, geometry_for  # noqa: E402


POSITIVE = (
    "whitespace-financial",
    "two-schemas",
    "prose-between",
    "wrapped-cells",
    "ruled-grid",
    "intro-paragraph",
    "currency-columns",
)
NEGATIVE = (
    "negative-bullet-list",
    "negative-numbered-list",
    "negative-chart",
    "negative-aligned-prose",
)
TOLERANCE = 0.01
IOU = 0.5


def _run(name: str):
    pdf_bytes = (FIXTURES / f"{name}.pdf").read_bytes()
    structure, _diagnostics, _elapsed = detect(pdf_bytes, geometry_for(pdf_bytes))
    golden = json.loads((FIXTURES / f"{name}.page-1.json").read_text(encoding="utf-8"))
    return golden.get("tables", []), structure["pages"][0]["tables"]


class SyntheticFixtureTests(unittest.TestCase):
    def test_every_expected_table_is_found_exactly_once(self) -> None:
        for name in POSITIVE:
            with self.subTest(fixture=name):
                expected, predicted = _run(name)
                matched, _used_expected, _used_predicted = match_tables(expected, predicted, IOU)
                self.assertEqual(len(matched), len(expected), f"{name}: recall")
                self.assertEqual(len(predicted), len(expected), f"{name}: precision")

    def test_boundaries_land_within_tolerance(self) -> None:
        for name in POSITIVE:
            with self.subTest(fixture=name):
                expected, predicted = _run(name)
                matched, _a, _b = match_tables(expected, predicted, IOU)
                for exp_index, pred_index, _score in matched:
                    for axis in ("column", "row"):
                        hits, produced, wanted = _boundary_counts(
                            _positions(expected[exp_index], axis),
                            _positions(predicted[pred_index], axis),
                            TOLERANCE,
                        )
                        self.assertEqual(hits, wanted, f"{name}: {axis} recall")
                        self.assertEqual(produced, wanted, f"{name}: {axis} precision")

    def test_header_row_counts_match(self) -> None:
        for name in POSITIVE:
            with self.subTest(fixture=name):
                expected, predicted = _run(name)
                matched, _a, _b = match_tables(expected, predicted, IOU)
                for exp_index, pred_index, _score in matched:
                    wanted = expected[exp_index].get("header") or {"rowCount": 0}
                    produced = predicted[pred_index].get("header") or {"rowCount": 0}
                    self.assertEqual(produced["rowCount"], wanted["rowCount"], name)

    def test_negatives_suggest_nothing(self) -> None:
        for name in NEGATIVE:
            with self.subTest(fixture=name):
                _expected, predicted = _run(name)
                self.assertEqual(predicted, [], f"{name}: false positive")


if __name__ == "__main__":
    unittest.main()


class EmittedStructureTests(unittest.TestCase):
    """The published model must satisfy the same rules the web decoder enforces."""

    def _check(self, tables: list[dict]) -> None:
        for table in tables:
            bounds = table["bounds"]
            self.assertGreater(bounds["width"], 0)
            self.assertGreater(bounds["height"], 0)
            self.assertLessEqual(bounds["x"] + bounds["width"], 1.0005)
            self.assertLessEqual(bounds["y"] + bounds["height"], 1.0005)
            self.assertIn(table["evidence"], ("ruled", "whitespace", "mixed"))
            self.assertGreaterEqual(table["confidence"], 0.0)
            self.assertLessEqual(table["confidence"], 1.0)

            self.assertGreaterEqual(len(table["columns"]), 2)
            edge = bounds["x"] - 0.005
            for column in table["columns"]:
                self.assertGreater(column["x1"], column["x0"])
                self.assertGreaterEqual(column["x0"], edge - 0.005)
                self.assertLessEqual(column["x1"], bounds["x"] + bounds["width"] + 0.005)
                edge = column["x1"]

            self.assertGreaterEqual(len(table["rows"]), 2)
            edge = bounds["y"] - 0.005
            for row in table["rows"]:
                self.assertGreater(row["y1"], row["y0"])
                self.assertGreaterEqual(row["y0"], edge - 0.005)
                self.assertLessEqual(row["y1"], bounds["y"] + bounds["height"] + 0.005)
                self.assertIn(row["kind"], ("header", "body"))
                self.assertIsInstance(row["merged"], bool)
                self.assertGreaterEqual(row["mergeConfidence"], 0.0)
                self.assertLessEqual(row["mergeConfidence"], 1.0)
                edge = row["y1"]

            header = table["header"]
            if header is not None:
                self.assertGreaterEqual(header["rowCount"], 1)
                self.assertLessEqual(header["rowCount"], len(table["rows"]))
                self.assertEqual(len(header["labels"]), len(table["columns"]))

    def test_every_fixture_emits_a_well_formed_model(self) -> None:
        for name in POSITIVE + NEGATIVE:
            with self.subTest(fixture=name):
                _expected, predicted = _run(name)
                self._check(predicted)


class PublishedCellTests(unittest.TestCase):
    """Every glyph must land in the right cell of the *published* geometry.

    The grid's own cell text applies rules a consumer never sees — a floated
    currency marker is attached to the amount it marks. What ships is the column
    and row bands, so this rebuilds each cell from those alone, the way the
    viewer's extractor does, and fails when a marker ends up in the wrong one.
    """

    def test_no_fixture_puts_a_marker_in_the_wrong_cell(self) -> None:
        from audit_table_cells import audit

        for name in POSITIVE:
            with self.subTest(fixture=name):
                report = audit(FIXTURES / f"{name}.pdf")
                self.assertEqual(
                    report["faults"],
                    0,
                    f"{name}: {report['pages']}",
                )
