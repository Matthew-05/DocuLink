"""Build the fs-values-v1 document model from text-geometry-v1."""
from __future__ import annotations

import base64
import gzip
import json
from statistics import median

from .context import context_for_text, document_context
from .spans import RecognizedSpan, recognize_spans


DETECTOR_VERSION = "fs-values-detector-1"


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


def _bounds(span: RecognizedSpan, source: list[dict | None]) -> dict | None:
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
    currency = span.currency or (inherited_currency if span.kind == "number" else "")
    if currency:
        value["currency"] = currency
    if span.date_precision:
        value["datePrecision"] = span.date_precision
    if span.date_order:
        value["dateOrder"] = span.date_order
    return value


def detect_fs_values(geometry: dict) -> dict:
    pages: list[dict] = []
    page_contexts: list[dict] = []
    prepared: list[tuple[int, list[tuple[str, list[dict | None]]], str]] = []
    for page in geometry.get("pages", []):
        lines = [_line_text(chars) for chars in _line_characters(page)]
        text = "\n".join(line for line, _ in lines)
        index = int(page.get("pageIndex", len(prepared)))
        prepared.append((index, lines, text))
        page_contexts.append(context_for_text(text))
    doc_context = document_context(page_contexts)

    for (page_index, lines, _), page_context in zip(prepared, page_contexts):
        context = dict(page_context)
        inherited_currency = context.get("currency") or doc_context.get("currency", "")
        values: list[dict] = []
        for text, source in lines:
            for span in recognize_spans(text):
                bounds = _bounds(span, source)
                if bounds is None:
                    continue
                identifier = f"fsv-p{page_index}-v{len(values)}"
                values.append(_value(span, bounds, identifier, inherited_currency))
        pages.append({"pageIndex": page_index, "context": context, "values": values})
    return {
        "version": 1,
        "coordinateSpace": "normalized",
        "detectorVersion": DETECTOR_VERSION,
        "documentContext": doc_context,
        "pages": pages,
    }


def fs_values_to_base64(model: dict) -> str:
    payload = json.dumps(model, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return base64.b64encode(gzip.compress(payload, compresslevel=6)).decode("ascii")
