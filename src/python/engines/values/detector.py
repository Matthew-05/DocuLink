"""Build the document-values-v1 model from text geometry.

Four categories, one decision each. A **value** measures and is published with
everything known about its reading. A **reference** identifies something outside
the document -- an invoice number, an area code, a citation. **Structure** is the
document indexing itself: the number in a note heading, the ordinal opening a
footnote. **Noise** is the residue: damage, and spans nothing could identify.

Whether a span becomes a click target is published per span, in `clickable`, and
decided by `categories.is_clickable`. The viewer reads that field and never
re-derives it, which is what lets a kind of span become capturable without any
change here or in the contract.

This is the value tier, and it runs on every document. What a financial document
additionally knows about itself arrives as claims from `engines.fs`; nothing
here imports that engine, and the model is complete without it.
"""
from __future__ import annotations

import base64
import gzip
import json
from collections import defaultdict
from decimal import Decimal
from typing import Iterable, Sequence

from .categories import (
    NOISE,
    REFERENCE,
    STRUCTURE,
    TOKEN_SHAPE_CATEGORY,
    VALUE,
    is_clickable,
)
from .claims import ClaimedSpan
from .evidence import classify, reference_kind
from .ids import SpanIds
from .lines import (
    PreparedDocument,
    PreparedLine,
    bounds_for,
    is_isolated_fragment,
    overlaps_ranges,
    prepare,
    span_height,
)
from .lists import detect_lists
from .spans import (
    RecognizedSpan,
    RejectedToken,
    recognize_magnitude_prefix,
    recognize_spans,
    recognize_wrapped_date,
    wrapped_date_heads,
    wrapped_date_years,
)


DETECTOR_VERSION = "document-values-detector-3"


def _value_payload(span: RecognizedSpan, bounds: dict, identifier: str, inherited_currency: str = "") -> dict:
    value = {
        "id": identifier,
        "kind": span.kind,
        "text": span.text,
        "bounds": bounds,
        "clickable": is_clickable(VALUE, span.kind),
        "confidence": span.confidence,
    }
    if span.normalized_value:
        value["normalizedValue"] = span.normalized_value
    if span.magnitude:
        value["magnitude"] = span.magnitude
    currency = span.currency or (inherited_currency if span.kind == "number" else "")
    if currency:
        value["currency"] = currency
    if span.date_precision:
        value["datePrecision"] = span.date_precision
    if span.date_order:
        value["dateOrder"] = span.date_order
    return value


def _scale_normalized_value(value: str, magnitude: int) -> str:
    scaled = format(Decimal(value) * magnitude, "f")
    if "." in scaled:
        scaled = scaled.rstrip("0").rstrip(".")
    return scaled or "0"


def _ends_line(span: RecognizedSpan, text: str) -> bool:
    return not text[span.end:].strip()


def _line_envelope_of(line: PreparedLine) -> tuple[float, float, float] | None:
    from .lines import line_envelope

    return line_envelope(line.source)


def _wrapped_dates(
    lines: Sequence[PreparedLine],
) -> tuple[
    dict[int, list[tuple[int, int, RecognizedSpan, dict, list[dict]]]],
    dict[int, list[tuple[int, int]]],
]:
    """Pair month/day heads with aligned years on the tightly wrapped line below."""
    from statistics import median

    values: dict[int, list[tuple[int, int, RecognizedSpan, dict, list[dict]]]] = {}
    occupied: dict[int, list[tuple[int, int]]] = {}
    for line_index in range(len(lines) - 1):
        line = lines[line_index]
        following = lines[line_index + 1]
        if line.page_index != following.page_index:
            continue
        heads = wrapped_date_heads(line.text)
        years = wrapped_date_years(following.text)
        if not heads or not years:
            continue
        current_envelope = _line_envelope_of(line)
        next_envelope = _line_envelope_of(following)
        if current_envelope is None or next_envelope is None:
            continue
        _current_top, current_bottom, current_height = current_envelope
        next_top, _next_bottom, next_height = next_envelope
        if next_top - current_bottom > max(0.004, median((current_height, next_height)) * 0.75):
            continue

        year_candidates: list[tuple[object, dict]] = []
        for year in years:
            if not is_isolated_fragment(year.start, year.end, following.source):
                continue
            year_bounds = bounds_for(year.start, year.end, following.source)
            if year_bounds is not None:
                year_candidates.append((year, year_bounds))
        used_years: set[int] = set()
        for head in heads:
            head_bounds = bounds_for(head.start, head.end, line.source)
            if head_bounds is None:
                continue
            head_center = head_bounds["x"] + head_bounds["width"] / 2
            padding = max(0.006, head_bounds["width"] * 0.15)
            aligned = [
                (index, year, bounds)
                for index, (year, bounds) in enumerate(year_candidates)
                if index not in used_years
                and head_bounds["x"] - padding
                <= bounds["x"] + bounds["width"] / 2
                <= head_bounds["x"] + head_bounds["width"] + padding
            ]
            if not aligned:
                continue
            year_index, year, year_bounds = min(
                aligned,
                key=lambda item: abs((item[2]["x"] + item[2]["width"] / 2) - head_center),
            )
            span = recognize_wrapped_date(head.text, year.text)
            if span is None:
                continue
            used_years.add(year_index)
            segments = [
                {"pageIndex": line.page_index, "text": head.text, "bounds": head_bounds},
                {"pageIndex": line.page_index, "text": year.text, "bounds": year_bounds},
            ]
            values.setdefault(line_index, []).append(
                (head.start, head.end, span, head_bounds, segments)
            )
            occupied.setdefault(line_index, []).append((head.start, head.end))
            occupied.setdefault(line_index + 1, []).append((year.start, year.end))
    return values, occupied


def _attach_wrapped_magnitude(
    value: dict,
    span: RecognizedSpan,
    page_index: int,
    following: PreparedLine,
) -> bool:
    if span.kind != "number" or span.magnitude:
        return False
    modifier = recognize_magnitude_prefix(following.text)
    if modifier is None:
        return False
    modifier_end, modifier_text, magnitude = modifier
    modifier_bounds = bounds_for(0, modifier_end, following.source)
    if modifier_bounds is None:
        return False

    value["text"] = f'{span.text.rstrip()} {modifier_text}'
    value["normalizedValue"] = _scale_normalized_value(span.normalized_value, magnitude)
    value["magnitude"] = magnitude
    value["segments"] = [
        {"pageIndex": page_index, "text": span.text, "bounds": value["bounds"]},
        {"pageIndex": following.page_index, "text": modifier_text, "bounds": modifier_bounds},
    ]
    return True


_DIAGNOSTIC_BUCKET = {
    REFERENCE: "value_references",
    STRUCTURE: "value_structure",
    NOISE: "value_noise",
}


def _count(diagnostics: dict | None, category: str, label: str) -> None:
    if diagnostics is None or category == VALUE:
        return
    bucket = _DIAGNOSTIC_BUCKET[category]
    diagnostics[bucket] = diagnostics.get(bucket, 0) + 1
    key = f"{bucket}_{label.replace('-', '_')}"
    diagnostics[key] = diagnostics.get(key, 0) + 1


def detect_values(
    geometry: dict | PreparedDocument,
    *,
    claims: Iterable[ClaimedSpan] = (),
    diagnostics: dict | None = None,
) -> dict:
    """Build the document-values-v1 model.

    `geometry` is a text-geometry-v1 model, or the `PreparedDocument` already
    read from one -- the financial tier prepares the document first, and passing
    it back avoids reading every page twice.

    `claims` are the spans a tier above has already understood. They are honoured
    exactly as `ClaimedSpan` describes and are optional: a document with no
    financial structure produces a complete model without any.
    """
    document = geometry if isinstance(geometry, PreparedDocument) else prepare(geometry)
    ids = SpanIds()

    by_page: dict[str, dict[int, list[dict]]] = {
        category: {index: [] for index in document.page_indexes}
        for category in (VALUE, REFERENCE, STRUCTURE, NOISE)
    }

    def publish(page_index: int, category: str, kind: str, text: str, bounds: dict, label: str, identifier: str = "") -> None:
        """Publish one non-value span under `label`: its kind, or its refusal.

        A reference and a structure span are named by what they are for; only a
        refusal is named by the shape the recognizer read, so noise keeps `kind`
        and carries the rule in `reason`.
        """
        payload = {
            "id": identifier or ids.assign(category, page_index, text, bounds),
            "kind": kind if category == NOISE else label,
            "text": text,
            "bounds": bounds,
            "clickable": is_clickable(category, label),
        }
        if category == NOISE:
            payload["reason"] = label
        by_page[category][page_index].append(payload)
        _count(diagnostics, category, label)

    claims_by_line: dict[int, list[ClaimedSpan]] = defaultdict(list)
    for claim in claims:
        claims_by_line[claim.line_index].append(claim)
    occupied_by_line: dict[int, list[tuple[int, int]]] = defaultdict(list)
    for line_index, line_claims in claims_by_line.items():
        occupied_by_line[line_index].extend(
            (claim.start, claim.end) for claim in line_claims
        )

    # Citations are published whole, in reading order, before anything is read
    # out of the lines they sit on.
    for line_index in sorted(claims_by_line):
        line = document.lines[line_index]
        for claim in sorted(claims_by_line[line_index], key=lambda item: item.start):
            if claim.category != REFERENCE or not claim.bounds:
                continue
            publish(
                line.page_index,
                REFERENCE,
                "text",
                claim.text,
                claim.bounds,
                claim.label,
                identifier=claim.identifier,
            )

    # Ordinal markers after the claims, so a heading that opens with a number
    # keeps the classification the tier above gave it. A marker numbers a list
    # rather than measuring anything, so it is structure.
    for marker in detect_lists(list(document.text_lines)).markers:
        line_index = marker.fragment.line_index
        if overlaps_ranges(marker.fragment.start, marker.fragment.end, occupied_by_line.get(line_index, [])):
            continue
        line = document.lines[line_index]
        bounds = bounds_for(marker.fragment.start, marker.fragment.end, line.source)
        if bounds is None:
            continue
        publish(line.page_index, STRUCTURE, "number", marker.text, bounds, marker.reason)
        occupied_by_line[line_index].append((marker.fragment.start, marker.fragment.end))

    def claim_label(line_index: int, start: int, end: int) -> str | None:
        """The structural role a fenced range imposes on anything inside it.

        The heading itself belongs to the catalogue that published it; what is
        printed inside one is a structure span here, so the integer in
        "Note 12" is never read as the figure twelve.
        """
        for claim in claims_by_line.get(line_index, []):
            if claim.category == STRUCTURE and start < claim.end and end > claim.start:
                return claim.label
        return None

    wrapped_dates, wrapped_occupied = _wrapped_dates(document.lines)

    for line_index, line in enumerate(document.lines):
        page_index = line.page_index
        inherited_currency = line.context.get("currency") or document.document_context.get("currency", "")
        fenced = occupied_by_line.get(line_index, [])

        candidates: list[tuple[int, int, RecognizedSpan, dict, list[dict] | None]] = list(
            wrapped_dates.get(line_index, [])
        )
        unparsed: list[RejectedToken] = []
        for span in recognize_spans(line.text, rejected=unparsed):
            if overlaps_ranges(span.start, span.end, wrapped_occupied.get(line_index, [])):
                continue
            bounds = bounds_for(span.start, span.end, line.source)
            if bounds is None:
                continue
            candidates.append((span.start, span.end, span, bounds, None))

        for token in unparsed:
            bounds = bounds_for(token.start, token.end, line.source)
            if bounds is None:
                continue
            label = claim_label(line_index, token.start, token.end)
            if label is not None:
                publish(page_index, STRUCTURE, "number", token.text, bounds, label)
                continue
            if overlaps_ranges(token.start, token.end, fenced):
                continue
            category, resolved = TOKEN_SHAPE_CATEGORY[token.reason]
            if category == REFERENCE:
                resolved = reference_kind(line.text, token.start, token.end)
            publish(page_index, category, "number", token.text, bounds, resolved)

        for start, end, span, bounds, segments in sorted(candidates, key=lambda item: item[0]):
            label = claim_label(line_index, start, end)
            if label is not None:
                publish(page_index, STRUCTURE, span.kind, span.text, bounds, label)
                continue
            if overlaps_ranges(start, end, fenced):
                continue
            # The recognizer reads one line, so "1" at the end of this line and
            # "million" at the start of the next is a bare integer until the two
            # are joined below. Classification runs first and has to be told.
            modifier_follows = (
                line_index + 1 < len(document.lines)
                and span.kind == "number"
                and not span.magnitude
                and _ends_line(span, line.text)
                and recognize_magnitude_prefix(document.lines[line_index + 1].text) is not None
            )
            category, resolved = classify(
                span,
                line=line.text,
                start=start,
                end=end,
                page_index=page_index,
                top=line.top,
                glyph_height=span_height(start, end, line.source),
                isolated=is_isolated_fragment(start, end, line.source),
                modifier_follows=modifier_follows,
                profile=document.profile,
            )
            if category != VALUE:
                publish(page_index, category, span.kind, span.text, bounds, resolved)
                continue
            identifier = ids.assign(VALUE, page_index, span.text, bounds)
            value = _value_payload(span, bounds, identifier, inherited_currency)
            if segments is not None:
                value["segments"] = segments
            if modifier_follows:
                _attach_wrapped_magnitude(value, span, page_index, document.lines[line_index + 1])
            by_page[VALUE][page_index].append(value)

    pages: list[dict] = []
    for page_index, page_context in zip(document.page_indexes, document.page_contexts):
        page = {
            "pageIndex": page_index,
            "context": dict(page_context),
            "values": by_page[VALUE][page_index],
        }
        for category, key in ((REFERENCE, "references"), (STRUCTURE, "structure"), (NOISE, "noise")):
            if by_page[category][page_index]:
                page[key] = by_page[category][page_index]
        pages.append(page)

    return {
        "version": 1,
        "coordinateSpace": "normalized",
        "detectorVersion": DETECTOR_VERSION,
        "documentContext": document.document_context,
        "pages": pages,
    }


def values_to_base64(model: dict) -> str:
    payload = json.dumps(model, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return base64.b64encode(gzip.compress(payload, compresslevel=6)).decode("ascii")
