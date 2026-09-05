"""Document-level facts that no single line can establish on its own.

A running footer and a superscript footnote marker both look like ordinary
content when you only have the line they sit on. What gives them away is
comparison: the footer recurs at the same height on page after page, and the
marker is set smaller than the text around it. This module makes one pass over
the document to establish those two facts for the evidence stage.
"""
from __future__ import annotations

import re
from collections import defaultdict
from dataclasses import dataclass
from statistics import median

from .spans import MONTHS


_DIGITS = re.compile(r"\d+")
_WORDS = re.compile(r"[^\W\d_]+")

# Words that carry no identity of their own. A line built only from these, month
# names and digits is a period caption -- "As of December 31, 2025", "For the
# fiscal year ended ..." -- which repeats on every page of a report but names the
# period its figures belong to, so it must stay readable.
_FUNCTION_WORDS = frozenset(
    "a an as at and by for from in of on or the to through ended ending end"
    " year years period periods quarter months month fiscal date dated".split()
)

# Furniture has to recur across a real share of the document before it is treated
# as furniture rather than as content that happens to repeat.
_REPEAT_SHARE = 0.25
_REPEAT_MINIMUM = 3
# Furniture holds its vertical position. A tenth of the page is coarse enough to
# survive a drifting baseline and fine enough not to merge a header into body text.
_BAND_HEIGHT = 0.1
# A footnote marker measures about two thirds of the text it annotates. The
# corpus shows markers at 0.65 and nothing at all between there and 0.8.
_SUPERSCRIPT_RATIO = 0.72


def _normalized(text: str) -> str:
    """Collapse the parts of a line that change from one page to the next."""
    return _DIGITS.sub("#", " ".join(text.split()))


def _band(top: float) -> int:
    return int(top / _BAND_HEIGHT)


def _is_identifiable(normalized: str) -> bool:
    """Whether a normalized line still names itself.

    Digit collapsing is what lets "Apple Inc. | 2025 Form 10-K | 7" match itself
    across eighty pages, but it also turns every numeric cell into the same "#",
    so a column of figures would otherwise convict itself. Month names are
    excluded too, along with the function words that surround them: a line of
    nothing but dates is a statement's column header repeated down a continued
    schedule, or a report's "As of" caption -- named periods either way, not
    page furniture.
    """
    return any(
        len(word) >= 2 and word.lower() not in MONTHS and word.lower() not in _FUNCTION_WORDS
        for word in _WORDS.findall(normalized)
    )


@dataclass(frozen=True)
class DocumentProfile:
    """What repeats, and how tall the ordinary glyph is on each page."""

    repeated_lines: frozenset[tuple[str, int]]
    glyph_heights: dict[int, float]

    def is_page_furniture(self, text: str, top: float) -> bool:
        """Whether this line is a running header or footer rather than content."""
        return (_normalized(text), _band(top)) in self.repeated_lines

    def is_superscript(self, page_index: int, height: float) -> bool:
        """Whether a glyph is set smaller than the page's ordinary text."""
        typical = self.glyph_heights.get(page_index, 0.0)
        return height > 0 and typical > 0 and height < typical * _SUPERSCRIPT_RATIO


EMPTY_PROFILE = DocumentProfile(repeated_lines=frozenset(), glyph_heights={})


def build_document_profile(
    lines: list[tuple[int, str, float]],
    glyph_heights: dict[int, list[float]],
) -> DocumentProfile:
    """Profile the document from its lines and the glyph heights on each page.

    `lines` supplies one `(pageIndex, text, top)` entry per text line.
    """
    pages = {page_index for page_index, _, _ in lines} | set(glyph_heights)
    if not pages:
        return EMPTY_PROFILE

    occurrences: dict[tuple[str, int], list[int]] = defaultdict(list)
    for page_index, text, top in lines:
        if not text.strip():
            continue
        normalized = _normalized(text)
        if _is_identifiable(normalized):
            occurrences[(normalized, _band(top))].append(page_index)

    threshold = max(_REPEAT_MINIMUM, len(pages) * _REPEAT_SHARE)
    repeated = frozenset(
        key
        for key, seen in occurrences.items()
        # Furniture is printed once per page. A body row that recurs because the
        # report reuses a phrase -- "50% Down", a payment term -- appears several
        # times on the same page, and that is what separates the two.
        if len(set(seen)) >= threshold and len(seen) == len(set(seen))
    )

    typical = {
        page_index: median(heights)
        for page_index, heights in glyph_heights.items()
        if heights
    }
    return DocumentProfile(repeated_lines=repeated, glyph_heights=typical)
