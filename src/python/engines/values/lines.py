"""Turn text-geometry characters into the lines every detector reasons about.

Pure geometry over text-geometry-v1: no OCR, PDF or Excel APIs, and no opinion
about what a number means. Both tiers read from here -- `engines.values` to
recognize spans, `engines.fs` to find headings and contents rows -- so the two
engines share one reading of the page rather than two that can disagree.
"""
from __future__ import annotations

from dataclasses import dataclass
from statistics import median

from .context import context_for_text, document_context
from .profile import DocumentProfile, build_document_profile


@dataclass(frozen=True)
class Fragment:
    """A range of characters within one prepared line."""

    line_index: int
    start: int
    end: int


@dataclass(frozen=True)
class TextFragment:
    start: int
    end: int
    text: str


@dataclass(frozen=True)
class TextLine:
    """One source line with the measurements a detector reasons about."""

    page_index: int
    text: str
    left: float
    right: float
    top: float
    bottom: float
    median_width: float
    median_height: float


@dataclass(frozen=True)
class PreparedLine:
    """One source line with the character boxes its text was built from.

    `source` is parallel to `text`: one entry per character, `None` where the
    reader inserted a space to represent a gap that no glyph occupies.
    """

    page_index: int
    text: str
    source: list[dict | None]
    context: dict
    top: float


@dataclass(frozen=True)
class PreparedDocument:
    page_indexes: tuple[int, ...]
    lines: tuple[PreparedLine, ...]
    text_lines: tuple[TextLine, ...]
    page_contexts: tuple[dict, ...]
    document_context: dict
    profile: DocumentProfile


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


def _visible(source: list[dict | None]) -> list[dict]:
    return [item for item in source if item is not None and str(item.get("char", "")).strip()]


def line_envelope(source: list[dict | None]) -> tuple[float, float, float] | None:
    """Top, bottom and typical glyph height for one line."""
    characters = _visible(source)
    if not characters:
        return None
    top = min(float(char["y"]) for char in characters)
    bottom = max(float(char["y"]) + float(char["height"]) for char in characters)
    heights = [float(char.get("height", 0)) for char in characters if float(char.get("height", 0)) > 0]
    return top, bottom, median(heights) if heights else max(0.001, bottom - top)


def line_left(source: list[dict | None]) -> float:
    return min((float(item["x"]) for item in _visible(source)), default=0.0)


def line_right(source: list[dict | None]) -> float:
    return max(
        (float(item["x"]) + float(item.get("width", 0)) for item in _visible(source)),
        default=0.0,
    )


def line_median_width(source: list[dict | None]) -> float:
    widths = [
        float(item.get("width", 0))
        for item in _visible(source)
        if float(item.get("width", 0)) > 0
    ]
    return median(widths) if widths else 0.005


def span_height(start: int, end: int, source: list[dict | None]) -> float:
    """The typical glyph height across a span, for comparison with its page."""
    heights = [
        float(item.get("height", 0))
        for item in _visible(source[start:end])
        if float(item.get("height", 0)) > 0
    ]
    return median(heights) if heights else 0.0


def bounds_for(start: int, end: int, source: list[dict | None]) -> dict | None:
    """The box enclosing the printing glyphs of one character range."""
    characters = _visible(source[start:end])
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


def enclosing_bounds(bounds: list[dict]) -> dict:
    left = min(item["x"] for item in bounds)
    top = min(item["y"] for item in bounds)
    right = max(item["x"] + item["width"] for item in bounds)
    bottom = max(item["y"] + item["height"] for item in bounds)
    return {"x": left, "y": top, "width": right - left, "height": bottom - top}


def overlaps_ranges(start: int, end: int, ranges: list[tuple[int, int]]) -> bool:
    return any(
        start < occupied_end and end > occupied_start
        for occupied_start, occupied_end in ranges
    )


def is_isolated_fragment(start: int, end: int, source: list[dict | None]) -> bool:
    """Whether a token sits in its own cell-sized island rather than in prose."""
    characters = _visible(source[start:end])
    if not characters:
        return False
    widths = [float(item.get("width", 0)) for item in characters if float(item.get("width", 0)) > 0]
    typical = median(widths) if widths else 0.005
    left = min(float(item["x"]) for item in characters)
    right = max(float(item["x"]) + float(item["width"]) for item in characters)
    before = next((item for item in reversed(_visible(source[:start]))), None)
    after = next((item for item in _visible(source[end:])), None)
    left_gap = (
        float("inf")
        if before is None
        else left - (float(before["x"]) + float(before["width"]))
    )
    right_gap = float("inf") if after is None else float(after["x"]) - right
    return left_gap >= typical * 2.0 and right_gap >= typical * 2.0


def fragment_bounds(fragment: Fragment, document: PreparedDocument) -> dict | None:
    line = document.lines[fragment.line_index]
    return bounds_for(fragment.start, fragment.end, line.source)


def fragment_text(fragment: Fragment, document: PreparedDocument) -> str:
    line = document.lines[fragment.line_index]
    return line.text[fragment.start:fragment.end]


def fragment_geometry(
    fragments: tuple[Fragment, ...],
    document: PreparedDocument,
    *,
    strict: bool,
) -> tuple[int, str, dict, list[dict]] | None:
    """Page, printed text, enclosing bounds and per-fragment segments.

    `strict` is how a heading and a contents row differ. A heading is one
    printed thing: if any line of it cannot be placed, the whole occurrence is
    dropped rather than published with a hole. A contents row is a set of
    independent cells, and a cell that cannot be placed costs only that cell.
    """
    placed = [
        (fragment, bounds, text)
        for fragment in fragments
        if (bounds := fragment_bounds(fragment, document)) is not None
        and (text := fragment_text(fragment, document).strip())
    ]
    if strict and len(placed) != len(fragments):
        return None
    if not placed:
        return None
    segments = [
        {
            "pageIndex": document.lines[fragment.line_index].page_index,
            "text": text,
            "bounds": bounds,
        }
        for fragment, bounds, text in placed
    ]
    return (
        segments[0]["pageIndex"],
        " ".join(segment["text"] for segment in segments),
        enclosing_bounds([bounds for _fragment, bounds, _text in placed]),
        segments,
    )


def prepare(geometry: dict) -> PreparedDocument:
    """Read a text-geometry-v1 model into lines, contexts and a page profile."""
    page_indexes: list[int] = []
    page_contexts: list[dict] = []
    per_page: list[list[tuple[str, list[dict | None]]]] = []
    glyph_heights: dict[int, list[float]] = {}
    for page in geometry.get("pages", []):
        lines = [_line_text(chars) for chars in _line_characters(page)]
        index = int(page.get("pageIndex", len(page_indexes)))
        page_indexes.append(index)
        per_page.append(lines)
        page_contexts.append(context_for_text("\n".join(line for line, _ in lines)))
        glyph_heights.setdefault(index, []).extend(
            height
            for height in (float(char.get("height", 0)) for char in page.get("characters", []))
            if height > 0
        )

    prepared: list[PreparedLine] = []
    text_lines: list[TextLine] = []
    profile_lines: list[tuple[int, str, float]] = []
    for page_index, lines, page_context in zip(page_indexes, per_page, page_contexts):
        for text, source in lines:
            envelope = line_envelope(source)
            top = envelope[0] if envelope is not None else 0.0
            prepared.append(PreparedLine(page_index, text, source, page_context, top))
            text_lines.append(
                TextLine(
                    page_index,
                    text,
                    line_left(source),
                    line_right(source),
                    top,
                    envelope[1] if envelope is not None else top,
                    line_median_width(source),
                    envelope[2] if envelope is not None else 0.0,
                )
            )
            profile_lines.append((page_index, text, top))

    return PreparedDocument(
        page_indexes=tuple(page_indexes),
        lines=tuple(prepared),
        text_lines=tuple(text_lines),
        page_contexts=tuple(page_contexts),
        document_context=document_context(page_contexts),
        profile=build_document_profile(profile_lines, glyph_heights),
    )
