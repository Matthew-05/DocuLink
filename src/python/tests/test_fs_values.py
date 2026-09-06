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


def _cell(text: str, *, y: float, line_index: int, x: float, height: float = 0.012) -> list[dict]:
    return _line(text, y=y, line_index=line_index, height=height, x=x)


def _contents_row(
    identifier: str,
    description: str,
    page: str,
    *,
    y: float,
    line_index: int,
) -> list[dict]:
    """One contents row printed as three cells sharing a band, as filings do."""
    return [
        *_cell(identifier, y=y, line_index=line_index, x=0.03),
        *_cell(description, y=y, line_index=line_index + 1, x=0.15),
        *_cell(page, y=y, line_index=line_index + 2, x=0.90),
    ]


def _contents_page(rows: list[tuple[str, str, str]], *, title: str = "TABLE OF CONTENTS") -> list[dict]:
    characters = _cell(title, y=0.04, line_index=0, x=0.40)
    for index, (identifier, description, page) in enumerate(rows):
        characters.extend(
            _contents_row(
                identifier,
                description,
                page,
                y=0.10 + index * 0.03,
                line_index=1 + index * 3,
            )
        )
    return characters


class ItemDetectionTests(unittest.TestCase):
    def test_contents_rows_build_the_catalog_and_are_noise(self) -> None:
        model = detect_fs_values(_geometry([_contents_page([
            ("Item 1.", "Business", "1"),
            ("Item 1A.", "Risk Factors", "5"),
            ("Item 7.", "Management’s Discussion and Analysis", "21"),
        ])]))

        self.assertEqual(
            [(item["identifier"], item["description"]) for item in model["items"]],
            [
                ("1", "Business"),
                ("1A", "Risk Factors"),
                ("7", "Management’s Discussion and Analysis"),
            ],
        )
        self.assertEqual(
            [item["tocEntries"][0]["printedPage"] for item in model["items"]],
            ["1", "5", "21"],
        )
        self.assertTrue(all(item["descriptionSource"] == "toc" for item in model["items"]))
        self.assertEqual(
            {entry["reason"] for entry in model["pages"][0]["noise"]},
            {"item-toc-entry"},
        )

    def test_a_contents_page_number_is_never_a_financial_value(self) -> None:
        # Every cell of the row is occupied, not only the identifier: the page
        # number is a bare integer sitting in its own column.
        published, _noise, _diagnostics = _detect([_contents_page([
            ("Item 1.", "Business", "1"),
            ("Item 2.", "Properties", "17"),
            ("Item 3.", "Legal Proceedings", "18"),
        ])])
        self.assertEqual(published, [])

    def test_a_letter_suffixed_identifier_survives_intact(self) -> None:
        model = detect_fs_values(_geometry([_contents_page([
            ("Item 1A.", "Risk Factors", "5"),
            ("Item 1B.", "Unresolved Staff Comments", "17"),
            ("Item 9C", "Disclosure Regarding Foreign Jurisdictions", "53"),
        ])]))
        self.assertEqual(
            [(item["identifier"], item["description"]) for item in model["items"]],
            [
                ("1A", "Risk Factors"),
                ("1B", "Unresolved Staff Comments"),
                ("9C", "Disclosure Regarding Foreign Jurisdictions"),
            ],
        )

    def test_a_body_heading_catalogs_an_item_the_contents_omits(self) -> None:
        model = detect_fs_values(_geometry([
            _contents_page([
                ("Item 5.", "Market for Registrant’s Common Equity", "19"),
                ("Item 7.", "Management’s Discussion and Analysis", "21"),
                ("Item 8.", "Financial Statements and Supplementary Data", "28"),
            ]),
            _line("Item 6. [Reserved]", y=0.10, line_index=0),
        ]))
        reserved = next(item for item in model["items"] if item["identifier"] == "6")
        self.assertEqual(reserved["description"], "[Reserved]")
        self.assertEqual(reserved["descriptionSource"], "heading")
        self.assertEqual(reserved["tocEntries"], [])
        self.assertEqual(reserved["headers"][0]["text"], "Item 6. [Reserved]")

    def test_the_contents_outranks_a_body_heading_that_disagrees(self) -> None:
        model = detect_fs_values(_geometry([
            _contents_page([
                ("Item 14.", "Principal Accountant Fees and Services", "53"),
                ("Item 15.", "Exhibit and Financial Statement Schedules", "54"),
                ("Item 16.", "Form 10-K Summary", "57"),
            ]),
            _line("Item 14. Principal Accounting Fees and Services", y=0.10, line_index=0),
        ]))
        item = next(item for item in model["items"] if item["identifier"] == "14")
        self.assertEqual(item["description"], "Principal Accountant Fees and Services")
        self.assertEqual(item["descriptionSource"], "toc")
        self.assertEqual(len(item["headers"]), 1)
        self.assertEqual(len(item["tocEntries"]), 1)

    def test_body_headings_without_a_page_column_are_not_a_contents_page(self) -> None:
        model = detect_fs_values(_geometry([[
            *_line("Item 10. Directors, Executive Officers and Corporate Governance", y=0.10, line_index=0),
            *_line("Item 11. Executive Compensation", y=0.20, line_index=1),
            *_line("Item 12. Security Ownership of Certain Beneficial Owners", y=0.30, line_index=2),
            *_line("Item 13. Certain Relationships and Related Transactions", y=0.40, line_index=3),
        ]]))
        self.assertTrue(model["items"])
        self.assertTrue(all(item["tocEntries"] == [] for item in model["items"]))
        self.assertTrue(all(item["descriptionSource"] == "heading" for item in model["items"]))

    def test_a_wrapped_contents_row_keeps_its_description_and_page(self) -> None:
        characters = _contents_page([
            ("Item 1.", "Business", "1"),
            ("Item 3.", "Legal Proceedings", "18"),
            ("Item 4.", "Mine Safety Disclosures", "18"),
        ])
        # A description too long for its column wraps, and the page number is
        # then printed beside the remainder rather than beside the identifier.
        characters.extend(_cell("Item 5.", y=0.20, line_index=40, x=0.03))
        characters.extend(_cell("Market for Registrant’s Common Equity, Related", y=0.20, line_index=41, x=0.15))
        characters.extend(_cell("Stockholder Matters", y=0.212, line_index=42, x=0.15))
        characters.extend(_cell("19", y=0.212, line_index=43, x=0.90))

        model = detect_fs_values(_geometry([characters]))
        item = next(item for item in model["items"] if item["identifier"] == "5")
        self.assertEqual(
            item["description"],
            "Market for Registrant’s Common Equity, Related Stockholder Matters",
        )
        self.assertEqual(item["tocEntries"][0]["printedPage"], "19")

    def test_a_rule_citation_is_not_an_item_reference(self) -> None:
        model = detect_fs_values(_geometry([
            _contents_page([
                ("Item 1.", "Business", "1"),
                ("Item 1A.", "Risk Factors", "5"),
                ("Item 15.", "Exhibit and Financial Statement Schedules", "54"),
            ]),
            [
                *_line("Exhibits required by Item 601 of Regulation S-K", y=0.10, line_index=0),
                *_line("omitted pursuant to Item 601(b)(2) of Regulation S-K", y=0.20, line_index=1),
                *_line("those discussed in Part I, Item 1A of this Form 10-K", y=0.30, line_index=2),
            ],
        ]))
        self.assertEqual(
            [(reference["identifier"], reference["text"]) for reference in model["itemReferences"]],
            [("1A", "Item 1A")],
        )
        self.assertEqual(model["itemReferences"][0]["part"], "I")
        self.assertEqual(model["itemReferences"][0]["description"], "Risk Factors")
        self.assertFalse(model["itemReferences"][0]["descriptionPresent"])

    def test_an_identifier_reused_across_parts_is_keyed_by_part(self) -> None:
        # A 10-Q prints Item 1 twice. They are different items, and collapsing
        # them would attach financial statements to legal proceedings.
        characters = _cell("TABLE OF CONTENTS", y=0.04, line_index=0, x=0.40)
        characters.extend(_cell("PART I", y=0.08, line_index=1, x=0.45))
        characters.extend(_contents_row("Item 1.", "Financial Statements", "3", y=0.12, line_index=2))
        characters.extend(_contents_row("Item 2.", "Management’s Discussion", "20", y=0.16, line_index=5))
        characters.extend(_cell("PART II", y=0.20, line_index=8, x=0.45))
        characters.extend(_contents_row("Item 1.", "Legal Proceedings", "30", y=0.24, line_index=9))
        characters.extend(_contents_row("Item 2.", "Unregistered Sales of Equity Securities", "31", y=0.28, line_index=12))

        model = detect_fs_values(_geometry([characters]))
        self.assertEqual(
            [(item["identifier"], item["part"], item["description"]) for item in model["items"]],
            [
                ("1", "I", "Financial Statements"),
                ("2", "I", "Management’s Discussion"),
                ("1", "II", "Legal Proceedings"),
                ("2", "II", "Unregistered Sales of Equity Securities"),
            ],
        )

    def test_a_reference_naming_no_part_is_left_unresolved_when_it_is_ambiguous(self) -> None:
        characters = _cell("TABLE OF CONTENTS", y=0.04, line_index=0, x=0.40)
        characters.extend(_cell("PART I", y=0.08, line_index=1, x=0.45))
        characters.extend(_contents_row("Item 1.", "Financial Statements", "3", y=0.12, line_index=2))
        characters.extend(_contents_row("Item 2.", "Management’s Discussion", "20", y=0.16, line_index=5))
        characters.extend(_cell("PART II", y=0.20, line_index=8, x=0.45))
        characters.extend(_contents_row("Item 1.", "Legal Proceedings", "30", y=0.24, line_index=9))
        characters.extend(_contents_row("Item 2.", "Unregistered Sales", "31", y=0.28, line_index=12))

        model = detect_fs_values(_geometry([
            characters,
            [
                *_line("as described in Item 1 of this report", y=0.10, line_index=0),
                *_line("see Part II, Item 1 for the matters", y=0.20, line_index=1),
            ],
        ]))
        self.assertEqual(
            [(reference["identifier"], reference["part"], reference["description"])
             for reference in model["itemReferences"]],
            [("1", "II", "Legal Proceedings")],
        )

    def test_a_contents_row_records_whether_a_table_corroborated_it(self) -> None:
        geometry = _geometry([_contents_page([
            ("Item 1.", "Business", "1"),
            ("Item 2.", "Properties", "17"),
            ("Item 3.", "Legal Proceedings", "18"),
        ])])
        tables = {
            "version": 1,
            "coordinateSpace": "normalized",
            "pages": [{
                "pageIndex": 0,
                "tables": [{
                    "id": "t0",
                    "bounds": {"x": 0.02, "y": 0.08, "width": 0.94, "height": 0.14},
                    "columns": [
                        {"x0": 0.02, "x1": 0.14},
                        {"x0": 0.14, "x1": 0.80},
                        {"x0": 0.80, "x1": 0.96},
                    ],
                }],
            }],
        }
        plain = detect_fs_values(geometry)
        corroborated = detect_fs_values(geometry, tables=tables)

        self.assertTrue(all(not item["tocEntries"][0]["corroborated"] for item in plain["items"]))
        self.assertTrue(all(item["tocEntries"][0]["corroborated"] for item in corroborated["items"]))
        self.assertEqual(
            [item["description"] for item in plain["items"]],
            [item["description"] for item in corroborated["items"]],
        )

    def test_a_contents_row_naming_no_item_is_not_folded_into_the_one_above(self) -> None:
        # A filing lists more than its items: signature pages and executive
        # officer sections sit in the same contents, at the identifier margin.
        # A wrapped description is indented past it, and that is the difference.
        characters = _contents_page([
            ("Item 2.", "Properties", "17"),
            ("Item 3.", "Legal Proceedings", "18"),
            ("Item 4.", "Mine Safety Disclosures", "18"),
        ])
        characters.extend(_cell("Information About our Executive Officers", y=0.19, line_index=40, x=0.03))
        characters.extend(_cell("29", y=0.19, line_index=41, x=0.90))

        model = detect_fs_values(_geometry([characters]))
        item = next(item for item in model["items"] if item["identifier"] == "4")
        self.assertEqual(item["description"], "Mine Safety Disclosures")
        self.assertEqual(item["tocEntries"][0]["printedPage"], "18")


def _marked_row(
    marker: str,
    body: str,
    *,
    y: float,
    line_index: int,
    marker_x: float = 0.03,
    body_x: float = 0.15,
) -> list[dict]:
    """A list item as geometry delivers one: the ordinal alone in its own cell."""
    return [
        *_cell(marker, y=y, line_index=line_index, x=marker_x),
        *_cell(body, y=y, line_index=line_index + 1, x=body_x),
    ]


def _marked_rows(rows: list[tuple[str, str]], *, start: float = 0.10) -> list[dict]:
    characters: list[dict] = []
    for index, (marker, body) in enumerate(rows):
        characters.extend(
            _marked_row(marker, body, y=start + index * 0.05, line_index=index * 2)
        )
    return characters


class ListMarkerTests(unittest.TestCase):
    """An ordinal that numbers a list item is not a quantity."""

    def test_an_exhibit_column_is_refused_whole(self) -> None:
        published, noise, _ = _detect([_marked_rows([
            ("3.1", "Restated Articles of Incorporation"),
            ("3.2", "Restated Bylaws of the Company"),
            ("4.1", "Description of Registered Securities"),
            ("10.1", "Incentive Compensation Plan"),
        ])])
        self.assertEqual(published, [])
        self.assertEqual(
            [(item["reason"], item["text"]) for item in noise],
            [
                ("list-marker", "3.1"),
                ("list-marker", "3.2"),
                ("list-marker", "4.1"),
                ("list-marker", "10.1"),
            ],
        )

    def test_a_column_that_only_ascends_stays_published(self) -> None:
        """Ages beside job titles look identical until you ask how they step."""
        published, _noise, _ = _detect([_marked_rows([
            ("48", "Senior Vice President and Chief Financial Officer"),
            ("50", "Chief Executive Officer of Web Services"),
            ("54", "Senior Vice President of Business Development"),
        ])])
        self.assertEqual(published, ["48", "50", "54"])

    def test_a_numbered_column_beside_figures_stays_published(self) -> None:
        """A marker leads prose. A cell whose neighbour is a figure leads nothing."""
        published, noise, _ = _detect([_marked_rows([
            ("1", "2,400"),
            ("2", "3,100"),
            ("3", "4,800"),
        ])])
        self.assertEqual(
            sorted(published), ["1", "2", "2,400", "3", "3,100", "4,800"]
        )
        self.assertEqual(noise, [])

    def test_an_inline_enumeration_is_refused(self) -> None:
        published, noise, _ = _detect([[
            *_line("Our competitors include: (1) online retailers of", y=0.1, line_index=0),
            *_line("physical goods; (2) publishers of digital media;", y=0.2, line_index=1),
            *_line("and (3) providers of commerce services.", y=0.3, line_index=2),
        ]])
        self.assertEqual(published, [])
        self.assertEqual(
            [item["reason"] for item in noise], ["list-marker"] * 3
        )


class FootnoteTests(unittest.TestCase):
    """A footnote's ordinal, and the indicator that points at it."""

    @staticmethod
    def _page() -> list[dict]:
        return [
            *_line("Number of Securities Underlying Options (1)", y=0.10, line_index=0),
            *_line("Total compensation reported for the year (2)", y=0.16, line_index=1),
            *_marked_row(
                "(1)", "Amounts are stated before forfeitures", y=0.60, line_index=2
            ),
            *_marked_row(
                "(2)", "Amounts include the retention bonus paid", y=0.66, line_index=4
            ),
            *_marked_row(
                "(3)", "Amounts exclude the value of health cover", y=0.72, line_index=6
            ),
        ]

    def test_a_footnote_block_and_its_indicators_are_refused(self) -> None:
        published, noise, _ = _detect([self._page()])
        self.assertEqual(published, [])
        self.assertEqual(
            sorted((item["reason"], item["text"]) for item in noise),
            [
                ("footnote-marker", "(1)"),
                ("footnote-marker", "(2)"),
                ("footnote-marker", "(3)"),
                ("footnote-reference", "(1)"),
                ("footnote-reference", "(2)"),
            ],
        )

    def test_an_indicator_without_a_footnote_block_stays_published(self) -> None:
        """Nothing may refuse a parenthesised figure on the strength of its shape."""
        published, _noise, _ = _detect([[
            *_line("Number of Securities Underlying Options (1)", y=0.10, line_index=0),
        ]])
        self.assertEqual(published, ["(1)"])

    def test_a_bracketed_negative_in_a_column_survives_the_footnotes(self) -> None:
        published, _noise, _ = _detect([[
            *self._page(),
            *_cell("Total state and local", y=0.30, line_index=8, x=0.03),
            *_cell("(1)", y=0.30, line_index=9, x=0.70),
            *_cell("(2)", y=0.36, line_index=10, x=0.70),
        ]])
        self.assertEqual(published, ["(1)", "(2)"])

    def test_a_bracketed_percentage_is_never_a_marker(self) -> None:
        published, _noise, _ = _detect([[
            *self._page(),
            *_line("Operating margin changed by (5)% over the year", y=0.24, line_index=8),
        ]])
        self.assertEqual(published, ["(5)%"])


def _annotated_row(
    label: str,
    figure: str,
    *,
    y: float,
    line_index: int,
    mark: str = "(1)",
    mark_height: float = 0.007,
) -> list[dict]:
    """A table row whose label carries a footnote mark in a cell of its own.

    `mark_height` is the whole test: a raised mark is an indicator, and the same
    mark at body size is a bracketed negative sitting in a column.
    """
    return [
        *_cell(label, y=y, line_index=line_index, x=0.03),
        *_cell(mark, y=y, line_index=line_index + 1, x=0.10, height=mark_height),
        *_cell(figure, y=y, line_index=line_index + 2, x=0.60),
    ]


class SharedFootnoteTests(unittest.TestCase):
    """One note may answer several rows, and then it has no chain to belong to."""

    @staticmethod
    def _page(*, mark_height: float = 0.007, rows: int = 2) -> list[dict]:
        characters: list[dict] = []
        for index in range(rows):
            characters.extend(
                _annotated_row(
                    "China",
                    "64,377",
                    y=0.10 + index * 0.15,
                    line_index=index * 3,
                    mark_height=mark_height,
                )
            )
        characters.extend(
            _marked_row(
                "(1)",
                "China includes Hong Kong and Taiwan",
                y=0.60,
                line_index=rows * 3,
            )
        )
        return characters

    def test_one_note_answers_every_mark_pointing_at_it(self) -> None:
        published, noise, _ = _detect([self._page()])
        self.assertEqual(published, ["64,377", "64,377"])
        self.assertEqual(
            sorted(item["reason"] for item in noise),
            ["footnote-marker", "footnote-reference", "footnote-reference"],
        )

    def test_a_single_mark_is_enough(self) -> None:
        _published, noise, _ = _detect([self._page(rows=1)])
        self.assertEqual(
            sorted(item["reason"] for item in noise),
            ["footnote-marker", "footnote-reference"],
        )

    def test_a_full_size_bracketed_figure_is_not_a_mark(self) -> None:
        """The guard: without a raised mark there is no note, and no note here
        means the column keeps its negatives and the paragraph keeps its number."""
        published, noise, _ = _detect([self._page(mark_height=0.012)])
        self.assertEqual(
            sorted(published), ["(1)", "(1)", "(1)", "64,377", "64,377"]
        )
        self.assertEqual(noise, [])

    def test_a_note_nothing_points_at_stays_published(self) -> None:
        published, noise, _ = _detect([_marked_row(
            "(1)", "China includes Hong Kong and Taiwan", y=0.60, line_index=0
        )])
        self.assertEqual(published, ["(1)"])
        self.assertEqual(noise, [])
