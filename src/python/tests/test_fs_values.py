from __future__ import annotations

import json
import unittest
from pathlib import Path

from engines.fs_values.detector import detect_fs_values
from engines.fs_values.spans import cut_token, recognize_spans, token_spans


FIXTURE = Path(__file__).parent / "fixtures" / "fs_values" / "span-oracle.json"


class SpanOracleTests(unittest.TestCase):
    def test_recognizer_matches_oracle(self) -> None:
        cases = json.loads(FIXTURE.read_text(encoding="utf-8"))
        for case in cases:
            with self.subTest(text=case["text"]):
                actual = []
                for span in recognize_spans(case["text"]):
                    item = {"kind": span.kind, "text": span.text}
                    if span.normalized_value:
                        item["normalizedValue"] = span.normalized_value
                    if span.currency:
                        item["currency"] = span.currency
                    if span.magnitude:
                        item["magnitude"] = span.magnitude
                    if span.date_precision:
                        item["datePrecision"] = span.date_precision
                    if span.date_order == "ambiguous":
                        item["dateOrder"] = span.date_order
                    actual.append(item)
                self.assertEqual(actual, case["spans"])

    def test_dates_win_over_the_numbers_inside_them(self) -> None:
        spans = recognize_spans("At December 31, 2025, cash was $ 1,200 or 4.5%.")
        self.assertEqual([span.kind for span in spans], ["date", "number", "percent"])


class DetectorTests(unittest.TestCase):
    def test_builds_bounds_and_page_context(self) -> None:
        text = "Amounts in millions USD  December 31, 2025  $ 1,200"
        chars = [
            {"char": char, "x": 0.02 + index * 0.008, "y": 0.10, "width": 0.008, "height": 0.02, "lineIndex": 0}
            for index, char in enumerate(text)
        ]
        model = detect_fs_values({"version": 1, "coordinateSpace": "normalized", "pages": [{"pageIndex": 0, "characters": chars}]})
        self.assertEqual(model["documentContext"]["currency"], "USD")
        self.assertEqual(model["pages"][0]["context"]["scale"], 1_000_000)
        self.assertEqual([value["kind"] for value in model["pages"][0]["values"]], ["date", "number"])
        self.assertEqual(model["pages"][0]["values"][1]["normalizedValue"], "1200")
        self.assertGreater(model["pages"][0]["values"][1]["bounds"]["width"], 0)

    def test_attaches_a_wrapped_modifier_across_pages(self) -> None:
        def characters(text: str, line_index: int = 0) -> list[dict]:
            return [
                {"char": char, "x": 0.02 + index * 0.01, "y": 0.10, "width": 0.01, "height": 0.02, "lineIndex": line_index}
                for index, char in enumerate(text)
            ]

        model = detect_fs_values({
            "version": 1,
            "coordinateSpace": "normalized",
            "pages": [
                {"pageIndex": 0, "characters": characters("$1.0")},
                {"pageIndex": 1, "characters": characters("million in revenue")},
            ],
        })
        value = model["pages"][0]["values"][0]
        self.assertEqual(value["text"], "$1.0 million")
        self.assertEqual(value["normalizedValue"], "1000000")
        self.assertEqual(value["magnitude"], 1_000_000)
        self.assertEqual([segment["pageIndex"] for segment in value["segments"]], [0, 1])

    def test_attaches_aligned_wrapped_date_headers(self) -> None:
        def placed(text: str, x: float, y: float, line_index: int) -> list[dict]:
            return [
                {
                    "char": char,
                    "x": x + index * 0.008,
                    "y": y,
                    "width": 0.008,
                    "height": 0.012,
                    "lineIndex": line_index,
                }
                for index, char in enumerate(text)
            ]

        characters = []
        for x, head, year in (
            (0.20, "September 27,", "2025"),
            (0.45, "September 28,", "2024"),
            (0.70, "September 30,", "2023"),
        ):
            characters.extend(placed(head, x, 0.10, 0))
            characters.extend(placed(year, x + 0.036, 0.11, 1))

        model = detect_fs_values({
            "version": 1,
            "coordinateSpace": "normalized",
            "pages": [{"pageIndex": 0, "characters": characters}],
        })
        values = model["pages"][0]["values"]
        self.assertEqual([value["text"] for value in values], [
            "September 27, 2025",
            "September 28, 2024",
            "September 30, 2023",
        ])
        self.assertEqual(values[0]["normalizedValue"], "2025-09-27")
        self.assertEqual(len(values[0]["segments"]), 2)
        self.assertEqual(values[0]["bounds"], values[0]["segments"][0]["bounds"])

    def test_does_not_attach_a_year_without_wrapped_leading(self) -> None:
        chars = [
            {"char": char, "x": 0.2 + index * 0.008, "y": 0.10, "width": 0.008, "height": 0.012, "lineIndex": 0}
            for index, char in enumerate("September 27,")
        ] + [
            {"char": char, "x": 0.236 + index * 0.008, "y": 0.20, "width": 0.008, "height": 0.012, "lineIndex": 1}
            for index, char in enumerate("2025")
        ]
        model = detect_fs_values({"pages": [{"pageIndex": 0, "characters": chars}]})
        self.assertNotIn("September 27, 2025", [value["text"] for value in model["pages"][0]["values"]])


def _line(text: str, *, y: float, line_index: int, height: float = 0.012, x: float = 0.02) -> list[dict]:
    return [
        {
            "char": char,
            "x": x + index * 0.008,
            "y": y,
            "width": 0.008,
            "height": height,
            "lineIndex": line_index,
        }
        for index, char in enumerate(text)
    ]


def _geometry(pages: list[list[dict]]) -> dict:
    return {
        "version": 1,
        "coordinateSpace": "normalized",
        "pages": [
            {"pageIndex": index, "characters": characters}
            for index, characters in enumerate(pages)
        ],
    }


def _detect(pages: list[list[dict]]) -> tuple[list[str], list[dict], dict]:
    """Published texts, the refused candidates and the diagnostics."""
    diagnostics: dict = {}
    model = detect_fs_values(_geometry(pages), diagnostics=diagnostics)
    published = [value["text"] for page in model["pages"] for value in page["values"]]
    noise = [item for page in model["pages"] for item in page.get("noise", [])]
    return published, noise, diagnostics


class TokenAlignmentTests(unittest.TestCase):
    """A value has to claim whole tokens, not cut one in half."""

    def test_a_joined_identifier_yields_no_value(self) -> None:
        for text, reason in (
            ("123-456-7890", "identifier"),
            ("FORM 10-K", "identifier"),
            ("94-2404110", "identifier"),
            ("3:13 PM", "identifier"),
            ("ASU 2024-03", "identifier"),
            ("(1)Includes $4", "alphanumeric"),
        ):
            with self.subTest(text=text):
                refused: list = []
                spans = recognize_spans(text, rejected=refused)
                self.assertNotIn(reason, [span.text for span in spans])
                self.assertIn(reason, [token.reason for token in refused])

    def test_a_value_may_still_span_several_tokens(self) -> None:
        for text, expected in (
            ("$ 50.14", "$ 50.14"),
            ("December 31, 2025", "December 31, 2025"),
            ("1.0 million", "1.0 million"),
            ("0.2 percent", "0.2 percent"),
        ):
            with self.subTest(text=text):
                self.assertEqual([span.text for span in recognize_spans(text)], [expected])

    def test_sentence_punctuation_is_not_part_of_the_token(self) -> None:
        self.assertEqual([span.text for span in recognize_spans("was $1,234.")], ["$1,234"])

    def test_a_cut_token_is_reported_whole_and_once(self) -> None:
        refused: list = []
        recognize_spans("call 123-456-7890 today", rejected=refused)
        self.assertEqual([(token.text, token.reason) for token in refused], [("123-456-7890", "identifier")])

    def test_a_fragmented_figure_is_reported_rather_than_guessed(self) -> None:
        refused: list = []
        spans = recognize_spans("1,2 34", rejected=refused)
        self.assertEqual([span.text for span in spans], ["34"])
        self.assertEqual([(token.text, token.reason) for token in refused], [("1,2", "partial-token")])

    def test_alignment_allows_only_trimmable_leftovers(self) -> None:
        text = "par value: 50,400,000 shares"
        tokens = token_spans(text)
        start = text.index("50,400,000")
        self.assertIsNone(cut_token(text, tokens, start, start + len("50,400,000")))

    def test_the_detector_publishes_nothing_from_a_form_number(self) -> None:
        published, refused, _ = _detect([_line("FORM 10-K", y=0.1, line_index=0)])
        self.assertEqual(published, [])
        self.assertEqual([item["reason"] for item in refused], ["identifier"])


class SuppressionTests(unittest.TestCase):
    def test_a_phone_number_never_becomes_a_negative(self) -> None:
        published, rejected, _ = _detect([_line("Cupertino, California (408) 996-1010", y=0.1, line_index=0)])
        self.assertEqual(published, [])
        self.assertIn("phone-context", {item["reason"] for item in rejected})

    def test_a_citation_year_is_not_a_period(self) -> None:
        published, _, _ = _detect([_line("of the Securities Exchange Act of 1934", y=0.1, line_index=0)])
        self.assertEqual(published, [])

    def test_a_year_reached_through_period_language_survives(self) -> None:
        published, _, _ = _detect([_line("for the fiscal year ended 2025", y=0.1, line_index=0)])
        self.assertEqual(published, ["2025"])

    def test_a_footnote_marker_set_smaller_than_its_page_is_dropped(self) -> None:
        page = _line("Total net sales were 1,234 in the period", y=0.10, line_index=0)
        page += _line("(1)", y=0.20, line_index=1, height=0.006)
        published, rejected, _ = _detect([page])
        self.assertEqual(published, ["1,234"])
        self.assertEqual([item["reason"] for item in rejected], ["superscript"])

    def test_an_identifier_label_only_condemns_what_follows_it(self) -> None:
        published, _, _ = _detect([_line("Total 1,234 CUSIP 037833100", y=0.1, line_index=0)])
        self.assertEqual(published, ["1,234"])

    def test_rejections_are_counted_by_reason(self) -> None:
        _, _, diagnostics = _detect([_line("FORM 10-K", y=0.1, line_index=0)])
        self.assertEqual(diagnostics["fs_value_rejected_identifier"], 1)
        self.assertEqual(diagnostics["fs_values_rejected"], 1)

    def test_noise_is_carried_beside_the_values_it_was_kept_from(self) -> None:
        model = detect_fs_values(_geometry([_line("FORM 10-K", y=0.1, line_index=0)]))
        page = model["pages"][0]
        self.assertEqual(page["values"], [])
        self.assertEqual(
            page["noise"],
            [{
                "id": "fsn-p0-n0",
                "kind": "number",
                "text": "10-K",
                "bounds": page["noise"][0]["bounds"],
                "reason": "identifier",
            }],
        )

    def test_a_page_that_refuses_nothing_carries_no_noise_key(self) -> None:
        model = detect_fs_values(_geometry([_line("Total 1,234", y=0.1, line_index=0)]))
        self.assertNotIn("noise", model["pages"][0])

    def test_noise_identifiers_are_distinct_from_value_identifiers(self) -> None:
        model = detect_fs_values(_geometry([_line("Total 1,234 of FORM 10-K", y=0.1, line_index=0)]))
        page = model["pages"][0]
        self.assertEqual([value["id"] for value in page["values"]], ["fsv-p0-v0"])
        self.assertEqual([item["id"] for item in page["noise"]], ["fsn-p0-n0"])


class PageFurnitureTests(unittest.TestCase):
    @staticmethod
    def _pages(footer: str, *, copies_per_page: int = 1) -> list[list[dict]]:
        pages = []
        for page_index in range(4):
            characters = _line(f"Net sales of 1,{page_index}00 in the period", y=0.10, line_index=0)
            for copy in range(copies_per_page):
                characters += _line(footer, y=0.95 + copy * 0.01, line_index=1 + copy)
            pages.append(characters)
        return pages

    def test_a_running_footer_is_not_content(self) -> None:
        published, rejected, _ = _detect(self._pages("Apple Inc. | 2025 Form 10-K | 7"))
        self.assertNotIn("2025", published)
        self.assertEqual({item["reason"] for item in rejected}, {"page-furniture", "identifier"})

    def test_a_period_caption_survives_repeating_on_every_page(self) -> None:
        published, _, _ = _detect(self._pages("As of December 31, 2025"))
        self.assertEqual(published.count("December 31, 2025"), 4)

    def test_a_row_printed_twice_on_a_page_is_content_not_furniture(self) -> None:
        published, _, _ = _detect(self._pages("Deposit 50% Down Payment 1,500", copies_per_page=2))
        self.assertEqual(published.count("1,500"), 8)

    def test_a_column_of_bare_figures_does_not_convict_itself(self) -> None:
        # Every numeric cell normalizes to the same text; only position and the
        # surviving words may distinguish furniture from data.
        pages = [_line("4,058.00", y=0.95, line_index=0) for _ in range(4)]
        published, _, _ = _detect(pages)
        self.assertEqual(published, ["4,058.00"] * 4)


class SectionHeadTests(unittest.TestCase):
    """A head that carries over the turn of a page, once the labels are aside."""

    @staticmethod
    def _document(heads: dict[int, str], pages: int = 16) -> list[list[dict]]:
        """A label on every page, body on every page, heads where asked.

        The label sits above the head, so "top" cannot mean the first line on
        the page -- it has to mean the first line that is not already furniture.
        """
        document = []
        for index in range(pages):
            characters = _line("Acme Corp Annual Report", y=0.04, line_index=0)
            if index in heads:
                characters += _line(heads[index], y=0.12, line_index=1)
            characters += _line(f"Revenue of 1,{index}00 in the period", y=0.40, line_index=2)
            document.append(characters)
        return document

    def test_a_head_repeated_over_a_run_is_refused(self) -> None:
        heads = {4: "Note 12 Income Taxes", 5: "Note 12 Income Taxes", 6: "Note 12 Income Taxes"}
        published, refused, _ = _detect(self._document(heads))
        self.assertNotIn("12", published)
        self.assertIn("running-section-head", {item["reason"] for item in refused})

    def test_a_head_that_changes_every_page_is_content(self) -> None:
        heads = {4: "Note 12 Income Taxes", 5: "Note 13 Leases", 6: "Note 14 Debt"}
        published, refused, _ = _detect(self._document(heads))
        self.assertNotIn("running-section-head", {item["reason"] for item in refused})
        self.assertEqual([t for t in published if t in {"12", "13", "14"}], ["12", "13", "14"])

    def test_a_continuation_marker_carries_a_head_on_its_own(self) -> None:
        heads = {4: "Note 12 Income Taxes", 5: "Note 12 Income Taxes (continued)"}
        _, refused, _ = _detect(self._document(heads))
        self.assertIn("running-section-head", {item["reason"] for item in refused})

    def test_a_head_below_the_body_is_not_a_head(self) -> None:
        document = []
        for index in range(16):
            characters = _line(f"Revenue of 1,{index}00 in the period", y=0.10, line_index=0)
            if index in (4, 5, 6):
                characters += _line("Note 12 Income Taxes", y=0.60, line_index=1)
            document.append(characters)
        _, refused, _ = _detect(document)
        self.assertNotIn("running-section-head", {item["reason"] for item in refused})


if __name__ == "__main__":
    unittest.main()
