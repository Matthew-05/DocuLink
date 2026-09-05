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


class NoteDetectionTests(unittest.TestCase):
    def test_full_note_headers_build_a_catalog_and_are_noise(self) -> None:
        model = detect_fs_values(_geometry([
            _line("Note 12 — Income Taxes", y=0.10, line_index=0),
            _line("NOTE IV. Fair Value Measurements", y=0.10, line_index=0),
            _line("Note 3.1: Revenue Recognition", y=0.10, line_index=0),
        ]))

        self.assertEqual(
            [(note["identifier"], note["description"]) for note in model["notes"]],
            [
                ("12", "Income Taxes"),
                ("IV", "Fair Value Measurements"),
                ("3.1", "Revenue Recognition"),
            ],
        )
        self.assertEqual(
            [page["noise"][0]["text"] for page in model["pages"]],
            [
                "Note 12 — Income Taxes",
                "NOTE IV. Fair Value Measurements",
                "Note 3.1: Revenue Recognition",
            ],
        )
        self.assertEqual(
            {page["noise"][0]["reason"] for page in model["pages"]},
            {"note-header"},
        )
        self.assertTrue(all(page["noise"][0]["kind"] == "text" for page in model["pages"]))
        self.assertTrue(all(not page["values"] for page in model["pages"]))

    def test_numbered_headers_without_note_are_scoped_to_a_notes_section(self) -> None:
        model = detect_fs_values(_geometry([[
            *_line("NOTES TO CONSOLIDATED FINANCIAL STATEMENTS", y=0.05, line_index=0),
            *_line("1 Description of the Business", y=0.10, line_index=1),
            *_line("2025. The adoption did not affect the statements", y=0.20, line_index=2),
            *_line("2 Summary of Significant Accounting Policies", y=0.30, line_index=3),
        ]]))

        self.assertEqual(
            [(note["identifier"], note["description"]) for note in model["notes"]],
            [
                ("1", "Description of the Business"),
                ("2", "Summary of Significant Accounting Policies"),
            ],
        )

        outside = detect_fs_values(_geometry([
            _line("1 Description of the Business", y=0.10, line_index=0),
        ]))
        self.assertEqual(outside["notes"], [])

    def test_a_bare_heading_split_across_source_lines_is_reassembled(self) -> None:
        model = detect_fs_values(_geometry([[
            *_line("NOTES TO CONSOLIDATED FINANCIAL STATEMENTS", y=0.05, line_index=0),
            *_line("2.", y=0.10, line_index=1, height=0.020, x=0.08),
            *_line("Summary of Significant Accounting Policies", y=0.103, line_index=2, x=0.12),
        ]]))

        self.assertEqual(model["notes"][0]["identifier"], "2")
        self.assertEqual(model["notes"][0]["description"], "Summary of Significant Accounting Policies")
        self.assertEqual(model["notes"][0]["headers"][0]["text"], "2. Summary of Significant Accounting Policies")

    def test_explicit_note_headings_disable_a_competing_bare_number_system(self) -> None:
        model = detect_fs_values(_geometry([[
            *_line("NOTES TO CONSOLIDATED FINANCIAL STATEMENTS", y=0.05, line_index=0),
            *_line("Note 8 — Leases", y=0.10, line_index=1),
            *_line("8-K", y=0.20, line_index=2),
            *_line("220-40: Disaggregation of Income Statement Expenses", y=0.30, line_index=3),
        ]]))

        self.assertEqual(
            [(note["identifier"], note["description"]) for note in model["notes"]],
            [("8", "Leases")],
        )

    def test_continuation_heading_attaches_to_the_existing_note(self) -> None:
        model = detect_fs_values(_geometry([
            _line("Note 6. Fair Value Measurements", y=0.10, line_index=0),
            _line("Note 6. Fair Value Measurements (Continued)", y=0.10, line_index=0),
            _line("Note 6 (continued)", y=0.10, line_index=0),
        ]))

        self.assertEqual(len(model["notes"]), 1)
        note = model["notes"][0]
        self.assertEqual(note["identifier"], "6")
        self.assertEqual(note["description"], "Fair Value Measurements")
        self.assertEqual(
            [header["continuation"] for header in note["headers"]],
            [False, True, True],
        )

    def test_a_wrapped_heading_is_stored_as_one_complete_heading(self) -> None:
        model = detect_fs_values(_geometry([[
            *_line("Note 2 — Summary of Significant", y=0.10, line_index=0),
            *_line("Accounting Policies", y=0.118, line_index=1, x=0.08),
            *_line("The Company recognizes revenue when control transfers.", y=0.18, line_index=2),
        ]]))

        header = model["notes"][0]["headers"][0]
        self.assertEqual(
            header["text"],
            "Note 2 — Summary of Significant Accounting Policies",
        )
        self.assertEqual(model["notes"][0]["description"], "Summary of Significant Accounting Policies")
        self.assertEqual(len(header["segments"]), 2)

    def test_a_following_body_subhead_is_not_part_of_the_note_heading(self) -> None:
        model = detect_fs_values(_geometry([[
            *_line("Note 2 — Revenue", y=0.10, line_index=0),
            *_line("Revenue Recognition", y=0.118, line_index=1),
        ]]))

        header = model["notes"][0]["headers"][0]
        self.assertEqual(header["text"], "Note 2 — Revenue")
        self.assertNotIn("segments", header)

    def test_narrative_references_resolve_descriptions_from_the_catalog(self) -> None:
        model = detect_fs_values(_geometry([
            [
                *_line("Note 7 — Income Taxes", y=0.10, line_index=0),
                *_line("Note 3.1 — Revenue Recognition", y=0.20, line_index=1),
                *_line("Note IV — Fair Value", y=0.30, line_index=2),
            ],
            [
                *_line("See Note 7 for details.", y=0.10, line_index=0),
                *_line("Refer to Note 7, “Income Taxes” for more information.", y=0.20, line_index=1),
                *_line("The policies in Notes 3.1 and IV apply.", y=0.30, line_index=2),
            ],
        ]))

        references = model["noteReferences"]
        self.assertEqual(
            [(reference["identifier"], reference["description"]) for reference in references],
            [
                ("7", "Income Taxes"),
                ("7", "Income Taxes"),
                ("3.1", "Revenue Recognition"),
                ("IV", "Fair Value"),
            ],
        )
        self.assertFalse(references[0]["descriptionPresent"])
        self.assertTrue(references[1]["descriptionPresent"])
        self.assertEqual(references[1]["sourceDescription"], "Income Taxes")
        self.assertEqual(references[2]["text"], "Notes 3.1 and IV")
        self.assertEqual(references[2]["bounds"], references[3]["bounds"])
        reference_noise = [
            item
            for item in model["pages"][1]["noise"]
            if item["reason"] == "note-reference"
        ]
        self.assertEqual(len(reference_noise), 3)
        self.assertTrue(all(item["kind"] == "text" for item in reference_noise))
        self.assertNotIn("7", [value["text"] for value in model["pages"][1]["values"]])

    def test_sentence_starting_with_a_note_reference_is_not_a_header(self) -> None:
        model = detect_fs_values(_geometry([[
            *_line("Note 12 — Income Taxes", y=0.10, line_index=0),
            *_line("Note 12 describes the uncertain tax positions.", y=0.30, line_index=1),
        ]]))

        self.assertEqual(len(model["notes"]), 1)
        self.assertEqual(len(model["notes"][0]["headers"]), 1)
        self.assertEqual(len(model["noteReferences"]), 1)
        self.assertEqual(model["noteReferences"][0]["text"], "Note 12")


if __name__ == "__main__":
    unittest.main()
