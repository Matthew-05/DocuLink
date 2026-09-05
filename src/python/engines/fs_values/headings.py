"""Shared mechanics for heading-anchored catalogues.

Financial-statement notes and SEC filing items are the same shape of thing: a
keyword, an identifier, an optional description, occurrences that repeat as
continuations, and narrative citations that resolve through a document-level
catalogue. This module owns that machinery so the two cannot drift apart.

A caller supplies the grammar -- what an identifier looks like, which keyword
introduces it -- and the policy deciding which candidate headings survive.
This module classifies text only; the detector owns geometry conversion and the
fs-values-v1 envelope.
"""
from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass


ROMAN = re.compile(
    r"M{0,4}(?:CM|CD|D?C{0,3})(?:XC|XL|L?X{0,3})(?:IX|IV|V?I{0,3})"
)
WORDS = re.compile(r"[^\W\d_]+", re.UNICODE)
CONTINUED = re.compile(
    r"\s*[\[(]?\s*(?:continued|cont(?:inue)?d?\.?|cont'd)\s*[\])]?\s*$",
    re.I,
)
HEADING_SEPARATOR = re.compile(r"^\s*(?:[.:\-–—]\s*)")
DESCRIPTION_SEPARATOR = re.compile(r"\s*(?:,|:|[\-–—])\s*")
TITLE_FUNCTION_WORDS = frozenset(
    "a an and as at by for from in of on or the to with without".split()
)
QUOTE_PAIRS = {
    '"': '"',
    "'": "'",
    "“": "”",
    "‘": "’",
    "�": "�",
}
TRIM_CHARACTERS = " \t“”‘’\"'"


@dataclass(frozen=True)
class HeadingLine:
    """One source line in document reading order."""

    page_index: int
    text: str
    left: float
    right: float
    top: float
    bottom: float
    median_width: float
    median_height: float


@dataclass(frozen=True)
class Fragment:
    """A half-open text range within one HeadingLine."""

    line_index: int
    start: int
    end: int


@dataclass(frozen=True)
class Extension:
    """The result of following a heading across the lines it wrapped onto."""

    description: str
    continuation: bool
    fragments: tuple[Fragment, ...]
    consumed: tuple[int, ...]


def normalize_decimal_identifier(raw: str, *, ceiling: int = 999) -> str | None:
    """A dotted decimal identifier in canonical form, or None if it is not one.

    The ceiling keeps dates and page-number prose from masquerading as headings
    while leaving ample room for a large catalogue.
    """
    if not re.fullmatch(r"\d+(?:\.\d+)*", raw):
        return None
    parts = [int(part) for part in raw.split(".")]
    if parts[0] > ceiling:
        return None
    return ".".join(str(part) for part in parts)


def normalize_roman_identifier(raw: str) -> str | None:
    roman = raw.upper()
    if roman and ROMAN.fullmatch(roman):
        return roman
    return None


def trimmed_range(text: str) -> tuple[int, int]:
    start = len(text) - len(text.lstrip())
    end = len(text.rstrip())
    return start, end


def without_continuation(text: str) -> tuple[str, bool]:
    match = CONTINUED.search(text)
    if match is None:
        return text.strip(), False
    return text[: match.start()].rstrip(), True


def clean_heading_description(rest: str) -> tuple[str, bool]:
    """Strip the separator after an identifier and any continuation marker."""
    description = HEADING_SEPARATOR.sub("", rest, count=1).strip()
    description, continuation = without_continuation(description)
    return description.strip(TRIM_CHARACTERS), continuation


def has_separator(rest: str) -> bool:
    return HEADING_SEPARATOR.match(rest) is not None


def looks_like_title(text: str, *, separator: bool = False) -> bool:
    stripped = text.strip()
    if not stripped:
        return True
    if len(stripped) > 180 or stripped.endswith(("?", "!", ";")):
        return False
    words = WORDS.findall(stripped)
    if not words or len(words) > 24:
        return False
    if separator:
        # A printed dash, colon or period after the identifier is strong heading
        # evidence and permits sentence-case accounting-policy titles.
        return not stripped.endswith(".")
    meaningful = [word for word in words if word.casefold() not in TITLE_FUNCTION_WORDS]
    if not meaningful:
        return False
    titled = sum(1 for word in meaningful if word[0].isupper())
    return titled / len(meaningful) >= 0.7 and not stripped.endswith(".")


def can_extend_heading(
    current: HeadingLine,
    following: HeadingLine,
    title_left: float,
    *,
    following_is_header: bool,
) -> bool:
    if current.page_index != following.page_index or following_is_header:
        return False
    gap = following.top - current.bottom
    typical_height = max(current.median_height, following.median_height, 0.001)
    if gap > max(0.004, typical_height * 0.9):
        return False
    overlap = max(0.0, min(current.bottom, following.bottom) - max(current.top, following.top))
    smaller_height = min(current.bottom - current.top, following.bottom - following.top)
    same_baseline = smaller_height > 0 and overlap / smaller_height >= 0.7
    if same_baseline:
        horizontal_gap = following.left - current.right
        return (
            following.left > current.left
            and horizontal_gap >= -max(0.005, current.median_width)
            and horizontal_gap <= max(0.04, current.median_width * 5)
            and looks_like_title(following.text)
        )
    # Wrapped title lines align under the title, not under the leading keyword.
    # This is what keeps an immediately following body subhead out of the
    # canonical description.
    alignment_tolerance = max(0.01, current.median_width * 2)
    if abs(following.left - title_left) > alignment_tolerance:
        return False
    return looks_like_title(following.text)


def extend_heading(
    lines: list[HeadingLine],
    line_index: int,
    description: str,
    *,
    fallback_prefix: str,
    is_header: "callable",
    lookahead: int = 3,
) -> Extension:
    """Follow a heading across the lines its title wrapped onto.

    A long title is occasionally wrapped independently of the body beneath it.
    Extension stops at the first line that is not close, title-shaped and
    aligned, and the look-ahead is capped so a run of body subheads cannot be
    swallowed wholesale. `is_header` reports whether a line index starts a
    heading of its own, which always ends the extension.
    """
    line = lines[line_index]
    start, end = trimmed_range(line.text)
    fragments = [Fragment(line_index, start, end)]
    description_offset = line.text.find(description) if description else -1
    title_left = (
        line.left + description_offset * line.median_width
        if description_offset >= 0
        else line.left + max(4, len(fallback_prefix)) * line.median_width
    )

    current = line
    consumed: list[int] = []
    parts: list[str] = []
    for following_index in range(line_index + 1, min(len(lines), line_index + lookahead)):
        following = lines[following_index]
        if not can_extend_heading(
            current,
            following,
            title_left,
            following_is_header=is_header(following_index),
        ):
            break
        next_start, next_end = trimmed_range(following.text)
        parts.append(following.text[next_start:next_end])
        fragments.append(Fragment(following_index, next_start, next_end))
        consumed.append(following_index)
        current = following
        title_left = following.left

    continuation = False
    if parts:
        description = " ".join([description, *parts]).strip()
        description, continuation = without_continuation(description)
    return Extension(description, continuation, tuple(fragments), tuple(consumed))


def canonical_description(descriptions: list[tuple[str, bool]]) -> str:
    """The description a catalogue entry publishes, given each occurrence's.

    Occurrences are (description, continuation) pairs. A continuation heading
    is a weaker witness than a full one, so it is consulted only when no full
    heading supplied a description.
    """
    values = [
        description for description, continuation in descriptions
        if description and not continuation
    ] or [description for description, _ in descriptions if description]
    if not values:
        return ""
    normalized = [" ".join(value.split()) for value in values]
    counts = Counter(value.casefold() for value in normalized)
    best = max(counts, key=lambda value: (counts[value], len(value)))
    return next(value for value in normalized if value.casefold() == best)


def description_after_reference(text: str, end: int) -> tuple[str, int]:
    """A description printed immediately after a citation, and where it ends."""
    separator = DESCRIPTION_SEPARATOR.match(text, end)
    if separator is None:
        return "", end
    cursor = separator.end()
    quote = text[cursor: cursor + 1]
    if quote in QUOTE_PAIRS:
        closing = QUOTE_PAIRS[quote]
        close_at = text.find(closing, cursor + 1)
        if close_at != -1:
            description = text[cursor + 1:close_at].strip()
            return (description, close_at + 1) if description else ("", end)

    stop = len(text)
    for punctuation in (",", ";", ".", ")"):
        found = text.find(punctuation, cursor)
        if found != -1:
            stop = min(stop, found)
    description = text[cursor:stop].strip(TRIM_CHARACTERS)
    if description and looks_like_title(description):
        return description, stop
    return "", end
