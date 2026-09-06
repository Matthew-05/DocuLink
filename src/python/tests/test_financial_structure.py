"""The financial tier: note and item catalogues, and what a document lacks."""
from __future__ import annotations

import unittest

from documents import detect, detect_pages, geometry as _geometry, cell as _cell, line as _line, label as _label, noise as _noise, published as _published, refused as _refused


def detect_model(model, *, tables=None, diagnostics=None):
    result = detect(model, tables=tables)
    if diagnostics is not None:
        diagnostics.update(result.diagnostics)
    return result


def _span_text(model, span_id: str) -> str:
    """The printed text of a citation, which lives on the reference span."""
    for page in model["pages"]:
        for item in page.get("references", []):
            if item["id"] == span_id:
                return item["text"]
    raise AssertionError(f"no reference span {span_id}")


def _detect(pages):
    """Published texts, everything refused, and the diagnostics."""
    result = detect_pages(pages)
    return _published(result), _refused(result), result.diagnostics

class NoteDetectionTests(unittest.TestCase):
    def test_full_note_headers_build_a_catalog_and_leave_structure(self) -> None:
        model = detect_model(_geometry([
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
        # The heading itself is structure and lives in the catalogue, printed
        # text and all. What the value tier publishes is only the integer inside
        # it, refused so it cannot be read as a figure.
        self.assertEqual(
            [note["headers"][0]["text"] for note in model["notes"]],
            [
                "Note 12 — Income Taxes",
                "NOTE IV. Fair Value Measurements",
                "Note 3.1: Revenue Recognition",
            ],
        )
        # A Roman identifier leaves nothing value-shaped behind, so its page
        # publishes no structure span at all.
        spans = [item for page in model["pages"] for item in page.get("structure", [])]
        self.assertEqual([item["text"] for item in spans], ["12", "3.1"])
        self.assertEqual({item["kind"] for item in spans}, {"note-header"})
        # Structure is drawn as a diagnostic, not captured.
        self.assertTrue(all(item["clickable"] is False for item in spans))
        self.assertTrue(all(not page["values"] for page in model["pages"]))

    def test_numbered_headers_without_note_are_scoped_to_a_notes_section(self) -> None:
        model = detect_model(_geometry([[
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

        outside = detect_model(_geometry([
            _line("1 Description of the Business", y=0.10, line_index=0),
        ]))
        self.assertEqual(outside["notes"], [])

    def test_a_bare_heading_split_across_source_lines_is_reassembled(self) -> None:
        model = detect_model(_geometry([[
            *_line("NOTES TO CONSOLIDATED FINANCIAL STATEMENTS", y=0.05, line_index=0),
            *_line("2.", y=0.10, line_index=1, height=0.020, x=0.08),
            *_line("Summary of Significant Accounting Policies", y=0.103, line_index=2, x=0.12),
        ]]))

        self.assertEqual(model["notes"][0]["identifier"], "2")
        self.assertEqual(model["notes"][0]["description"], "Summary of Significant Accounting Policies")
        self.assertEqual(model["notes"][0]["headers"][0]["text"], "2. Summary of Significant Accounting Policies")

    def test_explicit_note_headings_disable_a_competing_bare_number_system(self) -> None:
        model = detect_model(_geometry([[
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
        model = detect_model(_geometry([
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
        model = detect_model(_geometry([[
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
        model = detect_model(_geometry([[
            *_line("Note 2 — Revenue", y=0.10, line_index=0),
            *_line("Revenue Recognition", y=0.118, line_index=1),
        ]]))

        header = model["notes"][0]["headers"][0]
        self.assertEqual(header["text"], "Note 2 — Revenue")
        self.assertNotIn("segments", header)

    def test_narrative_references_resolve_descriptions_from_the_catalog(self) -> None:
        model = detect_model(_geometry([
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
        # A citation naming two notes is one printed span resolved twice, so
        # both entries point at the same reference.
        self.assertEqual(references[2]["spanId"], references[3]["spanId"])
        spans = {item["id"]: item for item in model["pages"][1].get("references", [])}
        self.assertEqual(spans[references[2]["spanId"]]["text"], "Notes 3.1 and IV")
        citations = [
            item for item in model["pages"][1].get("references", [])
            if item["kind"] == "note"
        ]
        self.assertEqual(len(citations), 3)
        self.assertTrue(all(reference["spanId"] in spans for reference in references[1:]))
        self.assertNotIn("7", [value["text"] for value in model["pages"][1]["values"]])

    def test_sentence_starting_with_a_note_reference_is_not_a_header(self) -> None:
        model = detect_model(_geometry([[
            *_line("Note 12 — Income Taxes", y=0.10, line_index=0),
            *_line("Note 12 describes the uncertain tax positions.", y=0.30, line_index=1),
        ]]))

        self.assertEqual(len(model["notes"]), 1)
        self.assertEqual(len(model["notes"][0]["headers"]), 1)
        self.assertEqual(len(model["noteReferences"]), 1)
        spans = {item["id"]: item for item in model["pages"][0].get("references", [])}
        self.assertEqual(spans[model["noteReferences"][0]["spanId"]]["text"], "Note 12")


if __name__ == "__main__":
    unittest.main()



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
    def test_contents_rows_build_the_catalog_and_leave_structure(self) -> None:
        model = detect_model(_geometry([_contents_page([
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
            {entry["kind"] for entry in model["pages"][0]["structure"]},
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
        model = detect_model(_geometry([_contents_page([
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
        model = detect_model(_geometry([
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
        model = detect_model(_geometry([
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
        model = detect_model(_geometry([[
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

        model = detect_model(_geometry([characters]))
        item = next(item for item in model["items"] if item["identifier"] == "5")
        self.assertEqual(
            item["description"],
            "Market for Registrant’s Common Equity, Related Stockholder Matters",
        )
        self.assertEqual(item["tocEntries"][0]["printedPage"], "19")

    def test_a_rule_citation_is_not_an_item_reference(self) -> None:
        model = detect_model(_geometry([
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
            [
                (reference["identifier"], _span_text(model, reference["spanId"]))
                for reference in model["itemReferences"]
            ],
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

        model = detect_model(_geometry([characters]))
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

        model = detect_model(_geometry([
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
        plain = detect_model(geometry)
        corroborated = detect_model(geometry, tables=tables)

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

        model = detect_model(_geometry([characters]))
        item = next(item for item in model["items"] if item["identifier"] == "4")
        self.assertEqual(item["description"], "Mine Safety Disclosures")
        self.assertEqual(item["tocEntries"][0]["printedPage"], "18")


