"""Document-level facts that no single line can establish on its own.

Page furniture looks like ordinary content when you only have the line it sits
on. What gives it away is comparison across pages, and there are two things to
compare: what the line *says* and where it *sits*.

Identity comes from the line's skeleton -- its text with every digit run masked
-- and from how each masked slot behaves across the pages the skeleton appears
on. A slot is either constant ("Form 10-K" is 10-K on every page) or it tracks
the page (a page number, whose value minus its page index never changes). A slot
that varies freely means the line is data, not furniture.

Role comes from position, but not from absolute height: a footer that follows
the end of the text rather than sitting at the foot of the sheet moves by half a
page between pages. What holds is that furniture sits outside the body -- above
every ordinary line, or below every ordinary line.

Document labels and page numbers are deliberately the only furniture inferred
from repetition. Financial-statement note headings and their narrative
references have their own explicit, structured detector.
"""
from __future__ import annotations

import re
from collections import defaultdict
from dataclasses import dataclass
from statistics import median

from .categories import PAGE_FURNITURE
from .spans import MONTHS


_DIGITS = re.compile(r"\d+")
_WORDS = re.compile(r"[^\W\d_]+")
# A line that is nothing but a small integer: the shape a bare page number takes.
_ONLY_NUMBER = re.compile(r"^[(\[]?-?\s*(\d{1,4})\s*[)\]]?[.,]?$")
# A page-number sequence runs through consecutive pages by its nature.
_SEQUENCE_MINIMUM = 3
# A statement's column headers are years, and consecutive pages of them climb by
# one exactly as page numbers do. Nothing else separates the two sequences, so a
# value that reads as a year is not a page number unless the document is long
# enough to actually reach that page.
_YEAR_RANGE = (1500, 2200)
# A page number is printed in the same place on every page. A year that happens
# to sit in a column header moves with the table it heads.
_PLACE_TOLERANCE = 120

# Furniture has to recur across a real share of the document before it is
# treated as furniture rather than as content that happens to repeat.
_REPEAT_SHARE = 0.25
_REPEAT_MINIMUM = 3
# A footnote marker measures about two thirds of the text it annotates. The
# corpus shows markers at 0.65 and nothing at all between there and 0.8.
_SUPERSCRIPT_RATIO = 0.72
# A page needs enough glyphs for its median to mean "ordinary text". A title
# page of two dozen words in two sizes does not: its median lands on the
# heading, and the ordinary text below reads as a footnote marker.
_REPRESENTATIVE_GLYPHS = 200

# Words that carry no identity of their own. A line built only from these, month
# names and digits is a period caption -- "As of December 31, 2025", "For the
# fiscal year ended ..." -- which repeats on every page of a report but names the
# period its figures belong to, so it must stay readable.
_FUNCTION_WORDS = frozenset(
    "a an as at and by for from in of on or the to through ended ending end"
    " year years period periods quarter months month fiscal date dated".split()
)


def skeleton(text: str) -> str:
    """The line with everything that may change from page to page masked out."""
    return _DIGITS.sub("#", " ".join(text.split()))


def _slots(text: str) -> list[int]:
    return [int(run) for run in _DIGITS.findall(" ".join(text.split()))]


def _names_itself(skeleton_text: str) -> bool:
    """Whether the skeleton still says anything specific.

    Masking digits is what lets "Apple Inc. | 2025 Form 10-K | 7" match itself
    across eighty pages, but it also turns every numeric cell into the same "#".
    Month names and the function words around them are excluded too: a line of
    nothing but dates names a period -- a statement's column header, a report's
    "As of" caption -- and a named period must stay readable.
    """
    return any(
        len(word) >= 2 and word.lower() not in MONTHS and word.lower() not in _FUNCTION_WORDS
        for word in _WORDS.findall(skeleton_text)
    )


@dataclass(frozen=True)
class DocumentProfile:
    """Which lines are furniture on which page, and the ordinary glyph height."""

    labels: dict[int, frozenset[str]]
    page_numbers: dict[int, frozenset[tuple[str, int]]]
    glyph_heights: dict[int, float]
    typical_glyph_height: float

    def furniture_reason(self, text: str, page_index: int, top: float) -> str:
        """Why this line is furniture on this page, or "" when it is content."""
        key = skeleton(text)
        if key in self.labels.get(page_index, frozenset()):
            return PAGE_FURNITURE
        if (" ".join(text.split()), _place(top)) in self.page_numbers.get(page_index, frozenset()):
            return PAGE_FURNITURE
        return ""

    def is_superscript(self, page_index: int, height: float) -> bool:
        """Whether a glyph is set smaller than the ordinary text around it."""
        typical = self.glyph_heights.get(page_index) or self.typical_glyph_height
        return height > 0 and typical > 0 and height < typical * _SUPERSCRIPT_RATIO


EMPTY_PROFILE = DocumentProfile(
    labels={}, page_numbers={}, glyph_heights={}, typical_glyph_height=0.0
)


def _place(top: float) -> int:
    """A line's vertical position, coarse enough to survive re-measurement."""
    return round(top * 2000)


def _page_numbers(
    page_lines: dict[int, list[tuple[float, str]]], page_count: int
) -> dict[int, set[tuple[str, int]]]:
    """Bare page numbers, found by the offset they share rather than by their text.

    A page number written on its own has the same skeleton as every figure in
    the document, so repetition cannot pick it out. What it does have is a value
    that tracks the page: subtract the page index and the same offset comes back
    on page after page. Numbers that merely happen to be numbers do not agree on
    an offset for three consecutive pages.
    """
    votes: dict[int, list[tuple[int, float, str]]] = defaultdict(list)
    for page, entries in page_lines.items():
        for top, text in entries:
            match = _ONLY_NUMBER.match(text)
            if match is None:
                continue
            value = int(match.group(1))
            if _YEAR_RANGE[0] <= value <= _YEAR_RANGE[1] and page_count < _YEAR_RANGE[0]:
                continue
            votes[value - page].append((page, top, text))

    found: dict[int, set[tuple[str, int]]] = defaultdict(set)
    for offset, entries in votes.items():
        pages = sorted({page for page, _, _ in entries})
        runs: list[list[int]] = []
        for page in pages:
            if runs and page == runs[-1][-1] + 1:
                runs[-1].append(page)
            else:
                runs.append([page])
        for run in runs:
            if len(run) < _SEQUENCE_MINIMUM:
                continue
            members = [entry for entry in entries if entry[0] in set(run)]
            middle = median(sorted(top for _, top, _ in members))
            settled = [
                min(
                    (entry for entry in members if entry[0] == page),
                    key=lambda entry: abs(entry[1] - middle),
                )
                for page in run
            ]
            if any(abs(_place(top) - _place(middle)) > _PLACE_TOLERANCE for _, top, _ in settled):
                continue
            # A page may hold more than one line matching the offset; the page
            # number is the one sitting where the rest of the sequence sits.
            for page in run:
                candidates = [entry for entry in members if entry[0] == page]
                page_number = min(candidates, key=lambda entry: abs(entry[1] - middle))
                found[page].add((page_number[2], _place(page_number[1])))
    return found


def _repeating_skeletons(page_lines: dict[int, list[str]]) -> set[str]:
    """Skeletons whose every numeric slot is constant or tracks the page.

    Printed once per page, because a body row that recurs -- a payment term used
    six times on the same page -- is content however often it appears.
    """
    occurrences: dict[str, list[tuple[int, list[int]]]] = defaultdict(list)
    for page_index, texts in page_lines.items():
        for text in texts:
            occurrences[skeleton(text)].append((page_index, _slots(text)))

    candidates: set[str] = set()
    for key, rows in occurrences.items():
        pages = [page for page, _ in rows]
        if len(pages) != len(set(pages)):
            continue
        columns = list(zip(*[values for _, values in rows])) if rows[0][1] else []
        tracks_page = False
        for column in columns:
            if len(set(column)) == 1:
                continue
            if len(set(value - page for value, page in zip(column, pages))) == 1:
                tracks_page = True
                continue
            break
        else:
            # An incrementing slot is its own evidence, so a bare page number
            # needs no words to identify it.
            if tracks_page or _names_itself(key):
                candidates.add(key)
    return candidates


def _edge_matter(texts: list[str], candidates: set[str]) -> set[int]:
    """Indices of candidate lines lying outside the page's body.

    Above every ordinary line, or below every ordinary line. Stated as a bound
    rather than an unbroken walk from the edge, so one unrelated line failing in
    the middle of a header block does not hide the rest of it.
    """
    ordinary = [index for index, text in enumerate(texts) if skeleton(text) not in candidates]
    if not ordinary:
        return set(range(len(texts)))
    first, last = ordinary[0], ordinary[-1]
    return {
        index
        for index in range(len(texts))
        if (index < first or index > last) and skeleton(texts[index]) in candidates
    }


def build_document_profile(
    lines: list[tuple[int, str, float]],
    glyph_heights: dict[int, list[float]],
) -> DocumentProfile:
    """Profile the document from its lines and the glyph heights on each page.

    `lines` supplies one `(pageIndex, text, top)` entry per text line.
    """
    pages = {page for page, _, _ in lines} | set(glyph_heights)
    if not pages:
        return EMPTY_PROFILE

    ordered: dict[int, list[str]] = {}
    by_page: dict[int, list[tuple[float, str]]] = defaultdict(list)
    for page, text, top in lines:
        if text.strip():
            by_page[page].append((top, " ".join(text.split())))
    for page, entries in by_page.items():
        entries.sort()
        ordered[page] = [text for _, text in entries]

    numbers = _page_numbers(by_page, len(pages))
    candidates = _repeating_skeletons(ordered)
    threshold = max(_REPEAT_MINIMUM, len(pages) * _REPEAT_SHARE)
    document_wide = {
        key
        for key in candidates
        if sum(1 for texts in ordered.values() if key in {skeleton(t) for t in texts}) >= threshold
    }

    labels: dict[int, frozenset[str]] = {}
    for page, texts in ordered.items():
        edge = _edge_matter(texts, document_wide)
        keys = {skeleton(texts[index]) for index in edge}
        if keys:
            labels[page] = frozenset(keys)

    typical = {
        page: median(heights)
        for page, heights in glyph_heights.items()
        if len(heights) >= _REPRESENTATIVE_GLYPHS
    }
    everything = [height for heights in glyph_heights.values() for height in heights]
    return DocumentProfile(
        labels=labels,
        page_numbers={page: frozenset(found) for page, found in numbers.items() if found},
        glyph_heights=typical,
        typical_glyph_height=median(everything) if everything else 0.0,
    )
