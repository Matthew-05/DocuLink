"""Build the fs-structure-v1 model from prepared text lines.

This is the financial tier. It runs on statements and filings alike -- they are
one tier because the difference between them is what a document happens to
contain, not what code should run on it. Each apparatus anchors itself: the item
catalogue needs a contents row or a body heading before it publishes anything,
so a compilation with no items yields none, which is the correct answer rather
than a failure. `apparatus` is what records the difference between the two.

The detector publishes two things. The model is the catalogues. `spans` is what
the value tier needs in order not to misread the same text: the ranges a heading
or a contents row occupies, so the integers inside them cannot become values,
and the citation spans, which are references and are published as such.
"""
from __future__ import annotations

import base64
import gzip
import json
from collections import Counter
from dataclasses import dataclass

from engines.values.categories import (
    ITEM,
    ITEM_HEADER,
    ITEM_TOC_ENTRY,
    NOTE,
    NOTE_HEADER,
    REFERENCE,
    STRUCTURE,
)
from engines.values.claims import ClaimedSpan
from engines.values.ids import span_id
from engines.values.lines import (
    Fragment,
    PreparedDocument,
    fragment_bounds,
    fragment_geometry,
    fragment_text,
)

from .items import detect_items
from .notes import detect_notes


DETECTOR_VERSION = "fs-structure-detector-1"


@dataclass(frozen=True)
class FsStructure:
    model: dict
    spans: tuple[ClaimedSpan, ...]


def _presence(found: int, *, searched: bool = True) -> dict:
    return {"searched": searched, "found": found}


def _document_class(items: list[dict], notes: list[dict]) -> str:
    if items:
        return "filing"
    if notes:
        return "statement"
    return "neither"


def _note_id(identifier: str) -> str:
    return f"note-{identifier}"


def _item_keys(catalog) -> dict[int, str]:
    """Stable ids for the item catalogue, keyed by identifier.

    The part is included only where the same identifier appears under more than
    one, which is what keeps a 10-Q's Part I Item 1 distinct from its Part II
    Item 1 without making every other id depend on a part it does not need.
    """
    counts = Counter(item.identifier for item in catalog)
    keys: dict[int, str] = {}
    for index, item in enumerate(catalog):
        if counts[item.identifier] > 1 and item.part:
            keys[index] = f"item-{item.part}-{item.identifier}"
        else:
            keys[index] = f"item-{item.identifier}"
    return keys


def detect_fs_structure(
    document: PreparedDocument,
    *,
    tables: dict | None = None,
) -> FsStructure:
    """Build the note and item catalogues, and the spans the value tier needs.

    `tables` is an optional table-structure-v1 model for the same document. It
    corroborates contents rows when it resolved the contents page; item
    detection reads the same rows from text geometry when it did not, so
    detection never depends on it.
    """
    lines = list(document.text_lines)
    spans: list[ClaimedSpan] = []

    def claim_range(fragment: Fragment, label: str) -> None:
        spans.append(
            ClaimedSpan(
                line_index=fragment.line_index,
                start=fragment.start,
                end=fragment.end,
                category=STRUCTURE,
                label=label,
                text=fragment_text(fragment, document).strip(),
                bounds=fragment_bounds(fragment, document) or {},
            )
        )

    # --- notes -------------------------------------------------------------
    note_detection = detect_notes(lines)
    note_descriptions = {note.identifier: note.description for note in note_detection.notes}
    note_payloads: list[dict] = []
    for note in note_detection.notes:
        identifier = _note_id(note.identifier)
        headers: list[dict] = []
        for header_index, header in enumerate(note.headers):
            placed = fragment_geometry(header.fragments, document, strict=True)
            if placed is None:
                continue
            page_index, text, bounds, segments = placed
            payload = {
                "id": f"{identifier}-h{header_index}",
                "pageIndex": page_index,
                "text": text,
                "bounds": bounds,
                "continuation": header.continuation,
            }
            if len(segments) > 1:
                payload["segments"] = segments
            headers.append(payload)
            for fragment in header.fragments:
                claim_range(fragment, NOTE_HEADER)
        if headers:
            note_payloads.append(
                {
                    "id": identifier,
                    "identifier": note.identifier,
                    "description": note.description,
                    "headers": headers,
                }
            )
    published_notes = {payload["identifier"] for payload in note_payloads}

    note_reference_payloads: list[dict] = []
    for occurrence_index, reference in enumerate(note_detection.references):
        bounds = fragment_bounds(reference.fragment, document)
        if bounds is None:
            continue
        resolvable = [
            identifier
            for identifier in reference.identifiers
            if identifier in published_notes
        ]
        if not resolvable:
            continue
        line = document.lines[reference.fragment.line_index]
        text = fragment_text(reference.fragment, document)
        reference_id = span_id(REFERENCE, line.page_index, text, bounds)
        spans.append(
            ClaimedSpan(
                line_index=reference.fragment.line_index,
                start=reference.fragment.start,
                end=reference.fragment.end,
                category=REFERENCE,
                label=NOTE,
                text=text,
                bounds=bounds,
                identifier=reference_id,
            )
        )
        for identifier_index, identifier in enumerate(resolvable):
            payload = {
                "id": f"noteref-{occurrence_index}-{identifier_index}",
                "spanId": reference_id,
                "noteId": _note_id(identifier),
                "identifier": identifier,
                "description": note_descriptions[identifier],
                "pageIndex": line.page_index,
                "descriptionPresent": bool(reference.source_description),
            }
            if reference.source_description:
                payload["sourceDescription"] = reference.source_description
            note_reference_payloads.append(payload)

    # --- items -------------------------------------------------------------
    tables_by_page: dict[int, list[dict]] = {}
    for page in (tables or {}).get("pages", []):
        tables_by_page[int(page.get("pageIndex", 0))] = page.get("tables", [])
    item_detection = detect_items(lines, tables_by_page)
    item_keys = _item_keys(item_detection.items)

    item_payloads: list[dict] = []
    published_items: dict[int, str] = {}
    contents_rows = 0
    for item_index, item in enumerate(item_detection.items):
        identifier = item_keys[item_index]
        headers: list[dict] = []
        for header_index, header in enumerate(item.headers):
            placed = fragment_geometry(header.fragments, document, strict=True)
            if placed is None:
                continue
            page_index, text, bounds, segments = placed
            payload = {
                "id": f"{identifier}-h{header_index}",
                "pageIndex": page_index,
                "text": text,
                "bounds": bounds,
                "continuation": header.continuation,
            }
            if len(segments) > 1:
                payload["segments"] = segments
            headers.append(payload)
            for fragment in header.fragments:
                claim_range(fragment, ITEM_HEADER)

        toc_entries: list[dict] = []
        for entry_index, row in enumerate(item.toc_entries):
            placed = fragment_geometry(row.fragments, document, strict=False)
            if placed is None:
                continue
            page_index, text, bounds, segments = placed
            payload = {
                "id": f"{identifier}-t{entry_index}",
                "pageIndex": page_index,
                "text": text,
                "bounds": bounds,
                "corroborated": row.corroborated,
            }
            if row.printed_page:
                payload["printedPage"] = row.printed_page
            if len(segments) > 1:
                payload["segments"] = segments
            toc_entries.append(payload)
            # Every cell is claimed, not just the identifier: the page number a
            # contents row prints is a value-shaped integer that is not a value.
            for fragment in row.fragments:
                claim_range(fragment, ITEM_TOC_ENTRY)

        if not headers and not toc_entries:
            continue
        payload = {
            "id": identifier,
            "identifier": item.identifier,
            "description": item.description,
            "descriptionSource": item.description_source,
            "headers": headers,
            "tocEntries": toc_entries,
        }
        if item.part:
            payload["part"] = item.part
        published_items[item_index] = identifier
        contents_rows += len(toc_entries)
        item_payloads.append(payload)

    item_reference_payloads: list[dict] = []
    for occurrence_index, reference in enumerate(item_detection.references):
        item_id = published_items.get(reference.catalog_index)
        if item_id is None:
            continue
        bounds = fragment_bounds(reference.fragment, document)
        if bounds is None:
            continue
        line = document.lines[reference.fragment.line_index]
        text = fragment_text(reference.fragment, document)
        reference_id = span_id(REFERENCE, line.page_index, text, bounds)
        spans.append(
            ClaimedSpan(
                line_index=reference.fragment.line_index,
                start=reference.fragment.start,
                end=reference.fragment.end,
                category=REFERENCE,
                label=ITEM,
                text=text,
                bounds=bounds,
                identifier=reference_id,
            )
        )
        payload = {
            "id": f"itemref-{occurrence_index}",
            "spanId": reference_id,
            "itemId": item_id,
            "identifier": reference.identifier,
            "description": item_detection.items[reference.catalog_index].description,
            "pageIndex": line.page_index,
            "descriptionPresent": bool(reference.source_description),
        }
        if reference.part:
            payload["part"] = reference.part
        if reference.source_description:
            payload["sourceDescription"] = reference.source_description
        item_reference_payloads.append(payload)

    parts = {payload["part"] for payload in item_payloads if payload.get("part")}

    model = {
        "version": 1,
        "coordinateSpace": "normalized",
        "detectorVersion": DETECTOR_VERSION,
        "documentClass": _document_class(item_payloads, note_payloads),
        "apparatus": {
            "notes": _presence(len(note_payloads)),
            "items": _presence(len(item_payloads)),
            "contents": _presence(contents_rows),
            "parts": _presence(len(parts)),
        },
        "notes": note_payloads,
        "noteReferences": note_reference_payloads,
        "items": item_payloads,
        "itemReferences": item_reference_payloads,
    }
    return FsStructure(model=model, spans=tuple(spans))


def structure_to_base64(model: dict) -> str:
    payload = json.dumps(model, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return base64.b64encode(gzip.compress(payload, compresslevel=6)).decode("ascii")
