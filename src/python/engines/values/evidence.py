"""Decide which of the three categories a recognized span belongs to.

`spans.py` answers "what shape is this text". This module answers the separate
question: is a well-formed number a value a reader would want to link, printed
data that identifies something, or an artifact of setting the page? A page
number, an area code, a statute year and a footnote marker are all well-formed
and none of them are values -- but only some of them are noise. An area code is
a phone number, and a phone number is printed data an auditor may want to
capture, so it is a reference and stays clickable.

Nothing here promotes; every rule either refuses a span or redirects it. The
verdict it returns travels into the model as the span's category, and the reason
or kind it carries is what the viewer's hover tip explains.
"""
from __future__ import annotations

import re

from .categories import (
    CITATION_YEAR,
    IDENTIFIER,
    NOISE,
    PHONE,
    POSTAL,
    REFERENCE,
    SECURITY_ID,
    SUPERSCRIPT,
    TAX_ID,
    VALUE,
)
from .profile import DocumentProfile
from .spans import RecognizedSpan


# A verdict is the category and, with it, the reference kind or the noise reason.
# A value carries neither, because there is nothing further to say about it.
Verdict = tuple[str, str]

KEEP: Verdict = (VALUE, "")


# Only the two phone shapes that cannot be mistaken for data: an explicit country
# code, or a parenthesised area code followed by a subscriber number.
_PHONE = re.compile(
    r"\+\d{1,3}[\s.\-]\d[\d\s.\-]{5,}\d"
    r"|\(\d{3}\)\s?\d{3}[\s.\-]\d{4}"
)

# Words that introduce a reference number rather than a quantity, each mapped to
# what the number it introduces actually identifies. Matched against the text
# before the span so a label cannot condemn the whole line.
_IDENTIFIER_CUES: tuple[tuple[str, re.Pattern[str]], ...] = (
    (
        PHONE,
        re.compile(r"\b(?:phone|telephone|tel|fax)\b[^.]{0,30}$", re.I),
    ),
    (
        POSTAL,
        re.compile(r"\b(?:zip|postal code)\b[^.]{0,30}$", re.I),
    ),
    (
        TAX_ID,
        re.compile(r"\bemployer identification\b[^.]{0,30}$", re.I),
    ),
    (
        SECURITY_ID,
        re.compile(
            r"\b(?:isin|cusip|sedol|ticker|trading symbol|symbol)\b[^.]{0,30}$",
            re.I,
        ),
    ),
    (
        IDENTIFIER,
        re.compile(
            r"\b(?:suite|p\.?\s?o\.?\s?box|file number|commission file"
            r"|registration (?:no|number))\b[^.]{0,30}$",
            re.I,
        ),
    ),
    # The number mark itself, standing apart from the number it names. "#7" is
    # one token and the recognizer refuses it on shape; "No. 7" and "# 7" are
    # two, so the mark has to be read as the cue it is. It must sit immediately
    # before the span -- a mark answers for the next number, not the sentence.
    (
        IDENTIFIER,
        re.compile(r"(?:\bnos?\.|[#\u2116])\s*$", re.I),
    ),
)

# A year reached through a citation is naming a law, not a period.
_CITATION_CONTEXT = re.compile(
    r"\b(?:act|section|rule|item|part|exhibit|form|schedule|chapter|article"
    r"|paragraph|regulation|pursuant|subtopic|topic|cik)"
    r"\b[^.]{0,25}$",
    re.I,
)


def reference_kind(line: str, start: int, end: int) -> str:
    """The most specific thing a reference-shaped token could be identifying.

    A token the recognizer refused never reaches `classify` -- there is no span
    to classify -- so the cues that would have named it are read here instead.
    An area code inside a printed phone number is a phone, not an unlabelled
    identifier, and saying so is what makes the hover tip worth reading.
    """
    if any(
        start < match.end() and end > match.start()
        for match in _PHONE.finditer(line)
    ):
        return PHONE
    before = line[:start]
    for kind, cue in _IDENTIFIER_CUES:
        if cue.search(before):
            return kind
    return IDENTIFIER


def classify(
    span: RecognizedSpan,
    *,
    line: str,
    start: int,
    end: int,
    page_index: int,
    top: float,
    glyph_height: float,
    profile: DocumentProfile,
) -> Verdict:
    """What this span is, and why.

    `start` and `end` locate the span within `line`. They are passed separately
    because a value wrapped across two lines carries the offsets of the fragment
    that appears on this one, not those of the logical span it belongs to.

    A token that could never be read as a value at all -- a form number, a
    product code -- is refused earlier, by `recognize_spans`, and reaches the
    model through its token shape rather than through this stage.
    """
    furniture = profile.furniture_reason(line, page_index, top)
    if furniture:
        return (NOISE, furniture)
    if any(
        start < match.end() and end > match.start()
        for match in _PHONE.finditer(line)
    ):
        return (REFERENCE, PHONE)
    before = line[:start]
    for kind, cue in _IDENTIFIER_CUES:
        if cue.search(before):
            return (REFERENCE, kind)
    if profile.is_superscript(page_index, glyph_height):
        return (NOISE, SUPERSCRIPT)
    if (
        span.kind == "date"
        and span.date_precision == "year"
        and _CITATION_CONTEXT.search(before)
    ):
        return (NOISE, CITATION_YEAR)
    return KEEP
