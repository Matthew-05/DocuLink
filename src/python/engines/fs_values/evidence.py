"""Decide whether a recognized span is worth publishing.

`spans.py` answers "what shape is this text". This module answers the separate
question the detector used to skip: is a well-formed number actually a value a
reader would want to link into a spreadsheet? A page number, an area code, a
statute year and a footnote marker are all well-formed and none of them are
values.

Each rule here suppresses; nothing here promotes. A suppressed candidate is
never published, and the reason it lost is reported through the detector's
`diagnostics` and `rejected` outputs so the overlay renderer can draw it.
"""
from __future__ import annotations

import re

from .profile import DocumentProfile
from .spans import RecognizedSpan, is_joined_fragment


JOINED_TOKEN = "joined-token"
PAGE_FURNITURE = "page-furniture"
PHONE_CONTEXT = "phone-context"
IDENTIFIER_CONTEXT = "identifier-context"
SUPERSCRIPT = "superscript"
CITATION_YEAR = "citation-year"

REASONS = (
    JOINED_TOKEN,
    PAGE_FURNITURE,
    PHONE_CONTEXT,
    IDENTIFIER_CONTEXT,
    SUPERSCRIPT,
    CITATION_YEAR,
)

# Only the two phone shapes that cannot be mistaken for data: an explicit country
# code, or a parenthesised area code followed by a subscriber number.
_PHONE = re.compile(
    r"\+\d{1,3}[\s.\-]\d[\d\s.\-]{5,}\d"
    r"|\(\d{3}\)\s?\d{3}[\s.\-]\d{4}"
)

# Words that introduce a reference number rather than a quantity. Matched against
# the text before the span so a label cannot condemn the whole line.
_IDENTIFIER_CONTEXT = re.compile(
    r"\b(?:phone|telephone|tel|fax|zip|postal code|suite|p\.?\s?o\.?\s?box"
    r"|isin|cusip|sedol|ticker|trading symbol|symbol"
    r"|employer identification|file number|commission file|registration (?:no|number))"
    r"\b[^.]{0,30}$",
    re.I,
)

# A year reached through a citation is naming a law, not a period.
_CITATION_CONTEXT = re.compile(
    r"\b(?:act|section|rule|item|part|exhibit|form|schedule|chapter|article"
    r"|paragraph|regulation|pursuant|subtopic|topic|cik)"
    r"\b[^.]{0,25}$",
    re.I,
)


def suppression_reason(
    span: RecognizedSpan,
    *,
    line: str,
    start: int,
    end: int,
    page_index: int,
    top: float,
    glyph_height: float,
    profile: DocumentProfile,
) -> str:
    """Why this span must not be published, or "" when nothing objects.

    `start` and `end` locate the span within `line`. They are passed separately
    because a value wrapped across two lines carries the offsets of the fragment
    that appears on this one, not those of the logical span it belongs to.
    """
    if is_joined_fragment(line, start, end):
        return JOINED_TOKEN
    if profile.is_page_furniture(line, top):
        return PAGE_FURNITURE
    if any(
        start < match.end() and end > match.start()
        for match in _PHONE.finditer(line)
    ):
        return PHONE_CONTEXT
    if _IDENTIFIER_CONTEXT.search(line[:start]):
        return IDENTIFIER_CONTEXT
    if profile.is_superscript(page_index, glyph_height):
        return SUPERSCRIPT
    if (
        span.kind == "date"
        and span.date_precision == "year"
        and _CITATION_CONTEXT.search(line[:start])
    ):
        return CITATION_YEAR
    return ""
