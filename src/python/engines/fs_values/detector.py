"""Build the fs-values-v1 document model from text-geometry-v1."""
from __future__ import annotations

import base64
import gzip
import json
from collections import defaultdict
from decimal import Decimal
from statistics import median

from .context import context_for_text, document_context
from .evidence import suppression_reason
from .notes import NoteFragment, NoteLine, detect_notes
from .profile import build_document_profile
from .reasons import NOTE_HEADER, NOTE_REFERENCE
from .spans import (
    RecognizedSpan,
    RejectedToken,
    TextFragment,
    recognize_magnitude_prefix,
    recognize_spans,
    recognize_wrapped_date,
    wrapped_date_heads,
    wrapped_date_years,
)


DETECTOR_VERSION = "fs-values-detector-6"


def _line_characters(page: dict) -> list[list[dict]]:
    by_line: dict[int, list[dict]] = {}
    for character in page.get("characters", []):
        by_line.setdefault(int(character.get("lineIndex", 0)), []).append(character)
    return [by_line[index] for index in sorted(by_line)]


def _line_text(characters: list[dict]) -> tuple[str, list[dict | None]]:
    ordered = sorted(characters, key=lambda char: (float(char.get("x", 0)), float(char.get("y", 0))))
    widths = [float(char.get("width", 0)) for char in ordered if float(char.get("width", 0)) > 0]
    typical = median(widths) if widths else 0.005
    output: list[str] = []
    source: list[dict | None] = []
    prior: dict | None = None
    for character in ordered:
        if prior is not None:
            gap = float(character.get("x", 0)) - (float(prior.get("x", 0)) + float(prior.get("width", 0)))
            if gap > typical * 1.25 and (not output or not output[-1].isspace()):
                output.append(" ")
                source.append(None)
        value = str(character.get("char", ""))
        for char in value:
            output.append(char)
            source.append(character)
        prior = character
    return "".join(output), source


def _bounds(span: RecognizedSpan | TextFragment, source: list[dict | None]) -> dict | None:
    characters = [item for item in source[span.start:span.end] if item is not None and str(item.get("char", "")).strip()]
    if not characters:
        return None
    left = min(float(char["x"]) for char in characters)
    top = min(float(char["y"]) for char in characters)
    right = max(float(char["x"]) + float(char["width"]) for char in characters)
    bottom = max(float(char["y"]) + float(char["height"]) for char in characters)
    return {
        "x": max(0.0, min(1.0, left)),
        "y": max(0.0, min(1.0, top)),
        "width": max(0.000001, min(1.0 - left, right - left)),
        "height": max(0.000001, min(1.0 - top, bottom - top)),
    }


def _value(span: RecognizedSpan, bounds: dict, identifier: str, inherited_currency: str = "") -> dict:
    value = {
        "id": identifier,
        "kind": span.kind,
        "text": span.text,
        "bounds": bounds,
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


def _line_envelope(source: list[dict | None]) -> tuple[float, float, float] | None:
    characters = [
        item
        for item in source
        if item is not None and str(item.get("char", "")).strip()
    ]
    if not characters:
        return None
    top = min(float(char["y"]) for char in characters)
    bottom = max(float(char["y"]) + float(char["height"]) for char in characters)
    heights = [float(char.get("height", 0)) for char in characters if float(char.get("height", 0)) > 0]
    return top, bottom, median(heights) if heights else max(0.001, bottom - top)


def _span_height(start: int, end: int, source: list[dict | None]) -> float:
    """The typical glyph height across a span, for comparison with its page."""
    heights = [
        float(item.get("height", 0))
        for item in source[start:end]
        if item is not None and str(item.get("char", "")).strip() and float(item.get("height", 0)) > 0
    ]
    return median(heights) if heights else 0.0


def _line_left(source: list[dict | None]) -> float:
    visible = [
        item
        for item in source
        if item is not None and str(item.get("char", "")).strip()
    ]
    return min((float(item["x"]) for item in visible), default=0.0)


def _line_right(source: list[dict | None]) -> float:
    visible = [
        item
        for item in source
        if item is not None and str(item.get("char", "")).strip()
    ]
    return max(
        (float(item["x"]) + float(item.get("width", 0)) for item in visible),
        default=0.0,
    )


def _line_median_width(source: list[dict | None]) -> float:
    widths = [
        float(item.get("width", 0))
        for item in source
        if item is not None
        and str(item.get("char", "")).strip()
        and float(item.get("width", 0)) > 0
    ]
    return median(widths) if widths else 0.005


def _fragment_bounds(
    fragment: NoteFragment,
    flat_lines: list[tuple[int, str, list[dict | None], dict, float]],
) -> dict | None:
    _page_index, _text, source, _context, _top = flat_lines[fragment.line_index]
    return _bounds(
        TextFragment(fragment.start, fragment.end, ""),
        source,
    )


def _fragments_text(
    fragments: tuple[NoteFragment, ...],
    flat_lines: list[tuple[int, str, list[dict | None], dict, float]],
) -> str:
    return " ".join(
        flat_lines[fragment.line_index][1][fragment.start:fragment.end].strip()
        for fragment in fragments
    )


def _enclosing_bounds(bounds: list[dict]) -> dict:
    left = min(item["x"] for item in bounds)
    top = min(item["y"] for item in bounds)
    right = max(item["x"] + item["width"] for item in bounds)
    bottom = max(item["y"] + item["height"] for item in bounds)
    return {
        "x": left,
        "y": top,
        "width": right - left,
        "height": bottom - top,
    }


def _overlaps_ranges(start: int, end: int, ranges: list[tuple[int, int]]) -> bool:
    return any(start < occupied_end and end > occupied_start for occupied_start, occupied_end in ranges)


def _noise(kind: str, text: str, bounds: dict, identifier: str, reason: str) -> dict:
    """Something the detector refused, and the rule that refused it."""
    return {
        "id": identifier,
        "kind": kind,
        "text": text,
        "bounds": bounds,
        "reason": reason,
    }


def _count_rejection(diagnostics: dict | None, reason: str) -> None:
    if diagnostics is None:
        return
    key = f"fs_value_rejected_{reason.replace('-', '_')}"
    diagnostics[key] = diagnostics.get(key, 0) + 1
    diagnostics["fs_values_rejected"] = diagnostics.get("fs_values_rejected", 0) + 1


def _is_isolated_fragment(fragment: TextFragment, source: list[dict | None]) -> bool:
    """Whether a token sits in its own cell-sized island rather than in prose."""
    characters = [
        item
        for item in source[fragment.start:fragment.end]
        if item is not None and str(item.get("char", "")).strip()
    ]
    if not characters:
        return False
    widths = [float(item.get("width", 0)) for item in characters if float(item.get("width", 0)) > 0]
    typical = median(widths) if widths else 0.005
    left = min(float(item["x"]) for item in characters)
    right = max(float(item["x"]) + float(item["width"]) for item in characters)
    before = next(
        (item for item in reversed(source[:fragment.start]) if item is not None and str(item.get("char", "")).strip()),
        None,
    )
    after = next(
        (item for item in source[fragment.end:] if item is not None and str(item.get("char", "")).strip()),
        None,
    )
    left_gap = (
        float("inf")
        if before is None
        else left - (float(before["x"]) + float(before["width"]))
    )
    right_gap = float("inf") if after is None else float(after["x"]) - right
    return left_gap >= typical * 2.0 and right_gap >= typical * 2.0


def _wrapped_dates(
    flat_lines: list[tuple[int, str, list[dict | None], dict, float]],
) -> tuple[
    dict[int, list[tuple[int, int, RecognizedSpan, dict, list[dict]]]],
    dict[int, list[tuple[int, int]]],
]:
    """Pair month/day heads with aligned years on the tightly wrapped line below."""
    values: dict[int, list[tuple[int, int, RecognizedSpan, dict, list[dict]]]] = {}
    occupied: dict[int, list[tuple[int, int]]] = {}
    for line_index in range(len(flat_lines) - 1):
        page_index, text, source, _, _top = flat_lines[line_index]
        next_page, next_text, next_source, _, _next_top = flat_lines[line_index + 1]
        if page_index != next_page:
            continue
        heads = wrapped_date_heads(text)
        years = wrapped_date_years(next_text)
        if not heads or not years:
            continue
        current_envelope = _line_envelope(source)
        next_envelope = _line_envelope(next_source)
        if current_envelope is None or next_envelope is None:
            continue
        _current_top, current_bottom, current_height = current_envelope
        next_top, _next_bottom, next_height = next_envelope
        if next_top - current_bottom > max(0.004, median((current_height, next_height)) * 0.75):
            continue

        year_candidates: list[tuple[TextFragment, dict]] = []
        for year in years:
            if not _is_isolated_fragment(year, next_source):
                continue
            year_bounds = _bounds(year, next_source)
            if year_bounds is not None:
                year_candidates.append((year, year_bounds))
        used_years: set[int] = set()
        for head in heads:
            head_bounds = _bounds(head, source)
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
                {"pageIndex": page_index, "text": head.text, "bounds": head_bounds},
                {"pageIndex": page_index, "text": year.text, "bounds": year_bounds},
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
    next_page_index: int,
    next_text: str,
    next_source: list[dict | None],
) -> bool:
    if span.kind != "number" or span.magnitude:
        return False
    modifier = recognize_magnitude_prefix(next_text)
    if modifier is None:
        return False
    modifier_end, modifier_text, magnitude = modifier
    modifier_span = RecognizedSpan(0, modifier_end, "number", modifier_text, span.confidence)
    modifier_bounds = _bounds(modifier_span, next_source)
    if modifier_bounds is None:
        return False

    value["text"] = f'{span.text.rstrip()} {modifier_text}'
    value["normalizedValue"] = _scale_normalized_value(span.normalized_value, magnitude)
    value["magnitude"] = magnitude
    value["segments"] = [
        {"pageIndex": page_index, "text": span.text, "bounds": value["bounds"]},
        {"pageIndex": next_page_index, "text": modifier_text, "bounds": modifier_bounds},
    ]
    return True


def detect_fs_values(geometry: dict, *, diagnostics: dict | None = None) -> dict:
    """Build the fs-values-v1 model from text geometry.

    Each page carries the values it publishes and, separately, the `noise` it
    refused -- recognized spans with the rule that ruled them out. Noise is a
    diagnostic: it is never a click target, and no consumer may treat it as a
    value. `diagnostics` receives a count per reason.
    """
    pages: list[dict] = []
    page_contexts: list[dict] = []
    prepared: list[tuple[int, list[tuple[str, list[dict | None]]], str]] = []
    glyph_heights: dict[int, list[float]] = defaultdict(list)
    for page in geometry.get("pages", []):
        lines = [_line_text(chars) for chars in _line_characters(page)]
        text = "\n".join(line for line, _ in lines)
        index = int(page.get("pageIndex", len(prepared)))
        prepared.append((index, lines, text))
        page_contexts.append(context_for_text(text))
        glyph_heights[index].extend(
            height
            for height in (float(char.get("height", 0)) for char in page.get("characters", []))
            if height > 0
        )
    doc_context = document_context(page_contexts)

    values_by_page: dict[int, list[dict]] = {page_index: [] for page_index, _, _ in prepared}
    noise_by_page: dict[int, list[dict]] = {page_index: [] for page_index, _, _ in prepared}
    flat_lines: list[tuple[int, str, list[dict | None], dict, float]] = []
    note_lines: list[NoteLine] = []
    profile_lines: list[tuple[int, str, float]] = []
    for (page_index, lines, _), page_context in zip(prepared, page_contexts):
        for text, source in lines:
            envelope = _line_envelope(source)
            top = envelope[0] if envelope is not None else 0.0
            flat_lines.append((page_index, text, source, page_context, top))
            note_lines.append(
                NoteLine(
                    page_index,
                    text,
                    _line_left(source),
                    _line_right(source),
                    top,
                    envelope[1] if envelope is not None else top,
                    _line_median_width(source),
                    envelope[2] if envelope is not None else 0.0,
                )
            )
            profile_lines.append((page_index, text, top))
    profile = build_document_profile(profile_lines, dict(glyph_heights))

    note_detection = detect_notes(note_lines)
    note_ids = {
        note.identifier: f"fs-note-{index}"
        for index, note in enumerate(note_detection.notes)
    }
    note_descriptions = {
        note.identifier: note.description
        for note in note_detection.notes
    }
    note_payloads: list[dict] = []
    note_noise_by_line: dict[int, list[tuple[int, str, str, dict]]] = defaultdict(list)
    note_occupied_by_line: dict[int, list[tuple[int, int]]] = defaultdict(list)

    for note_index, note in enumerate(note_detection.notes):
        headers: list[dict] = []
        for header_index, header in enumerate(note.headers):
            segment_bounds = [
                bounds
                for fragment in header.fragments
                if (bounds := _fragment_bounds(fragment, flat_lines)) is not None
            ]
            if len(segment_bounds) != len(header.fragments):
                continue
            first_fragment = header.fragments[0]
            page_index = flat_lines[first_fragment.line_index][0]
            text = _fragments_text(header.fragments, flat_lines)
            header_payload = {
                "id": f"fsnh-n{note_index}-h{header_index}",
                "pageIndex": page_index,
                "text": text,
                "bounds": _enclosing_bounds(segment_bounds),
                "continuation": header.continuation,
            }
            if len(header.fragments) > 1:
                header_payload["segments"] = [
                    {
                        "pageIndex": flat_lines[fragment.line_index][0],
                        "text": flat_lines[fragment.line_index][1][fragment.start:fragment.end].strip(),
                        "bounds": bounds,
                    }
                    for fragment, bounds in zip(header.fragments, segment_bounds)
                ]
            headers.append(header_payload)
            note_noise_by_line[first_fragment.line_index].append(
                (first_fragment.start, NOTE_HEADER, text, header_payload["bounds"])
            )
            for fragment in header.fragments:
                note_occupied_by_line[fragment.line_index].append((fragment.start, fragment.end))
        if headers:
            note_payloads.append(
                {
                    "id": note_ids[note.identifier],
                    "identifier": note.identifier,
                    "description": note.description,
                    "headers": headers,
                }
            )

    note_reference_payloads: list[dict] = []
    for occurrence_index, reference in enumerate(note_detection.references):
        bounds = _fragment_bounds(reference.fragment, flat_lines)
        if bounds is None:
            continue
        line_index = reference.fragment.line_index
        page_index, line_text, _source, _context, _top = flat_lines[line_index]
        text = line_text[reference.fragment.start:reference.fragment.end]
        note_noise_by_line[line_index].append(
            (reference.fragment.start, NOTE_REFERENCE, text, bounds)
        )
        note_occupied_by_line[line_index].append(
            (reference.fragment.start, reference.fragment.end)
        )
        for identifier_index, identifier in enumerate(reference.identifiers):
            payload = {
                "id": f"fsnr-p{page_index}-r{occurrence_index}-n{identifier_index}",
                "noteId": note_ids[identifier],
                "identifier": identifier,
                "description": note_descriptions[identifier],
                "pageIndex": page_index,
                "text": text,
                "bounds": bounds,
                "descriptionPresent": bool(reference.source_description),
            }
            if reference.source_description:
                payload["sourceDescription"] = reference.source_description
            note_reference_payloads.append(payload)

    wrapped_dates, wrapped_occupied = _wrapped_dates(flat_lines)

    for line_index, (page_index, text, source, page_context, top) in enumerate(flat_lines):
        for _start, reason, note_text, bounds in sorted(
            note_noise_by_line.get(line_index, []),
            key=lambda item: item[0],
        ):
            page_noise = noise_by_page[page_index]
            page_noise.append(
                _noise("text", note_text, bounds, f"fsn-p{page_index}-n{len(page_noise)}", reason)
            )
            _count_rejection(diagnostics, reason)

        context = dict(page_context)
        inherited_currency = context.get("currency") or doc_context.get("currency", "")
        candidates: list[tuple[int, int, RecognizedSpan, dict, list[dict] | None]] = list(
            wrapped_dates.get(line_index, [])
        )
        unparsed: list[RejectedToken] = []
        for span in recognize_spans(text, rejected=unparsed):
            if _overlaps_ranges(span.start, span.end, wrapped_occupied.get(line_index, [])):
                continue
            if _overlaps_ranges(span.start, span.end, note_occupied_by_line.get(line_index, [])):
                continue
            bounds = _bounds(span, source)
            if bounds is None:
                continue
            candidates.append((span.start, span.end, span, bounds, None))
        for token in unparsed:
            if _overlaps_ranges(token.start, token.end, note_occupied_by_line.get(line_index, [])):
                continue
            bounds = _bounds(token, source)
            if bounds is None:
                continue
            page_noise = noise_by_page[page_index]
            page_noise.append(
                _noise("number", token.text, bounds, f"fsn-p{page_index}-n{len(page_noise)}", token.reason)
            )
            _count_rejection(diagnostics, token.reason)

        for start, end, span, bounds, segments in sorted(candidates, key=lambda item: item[0]):
            if _overlaps_ranges(start, end, note_occupied_by_line.get(line_index, [])):
                continue
            reason = suppression_reason(
                span,
                line=text,
                start=start,
                end=end,
                page_index=page_index,
                top=top,
                glyph_height=_span_height(start, end, source),
                profile=profile,
            )
            if reason:
                page_noise = noise_by_page[page_index]
                page_noise.append(
                    _noise(span.kind, span.text, bounds, f"fsn-p{page_index}-n{len(page_noise)}", reason)
                )
                _count_rejection(diagnostics, reason)
                continue
            page_values = values_by_page[page_index]
            identifier = f"fsv-p{page_index}-v{len(page_values)}"
            value = _value(span, bounds, identifier, inherited_currency)
            if segments is not None:
                value["segments"] = segments
            if line_index + 1 < len(flat_lines) and span.kind == "number" and not span.magnitude and _ends_line(span, text):
                next_page, next_text, next_source, _, _next_top = flat_lines[line_index + 1]
                _attach_wrapped_magnitude(value, span, page_index, next_page, next_text, next_source)
            page_values.append(value)

    for (page_index, _, _), page_context in zip(prepared, page_contexts):
        page = {
            "pageIndex": page_index,
            "context": dict(page_context),
            "values": values_by_page[page_index],
        }
        if noise_by_page[page_index]:
            page["noise"] = noise_by_page[page_index]
        pages.append(page)
    return {
        "version": 1,
        "coordinateSpace": "normalized",
        "detectorVersion": DETECTOR_VERSION,
        "documentContext": doc_context,
        "notes": note_payloads,
        "noteReferences": note_reference_payloads,
        "pages": pages,
    }


def fs_values_to_base64(model: dict) -> str:
    payload = json.dumps(model, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return base64.b64encode(gzip.compress(payload, compresslevel=6)).decode("ascii")
