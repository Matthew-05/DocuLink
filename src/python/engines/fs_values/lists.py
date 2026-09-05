"""Ordinals that number something rather than measure it.

A filing prints small integers that look exactly like values and are not. An
exhibit index runs ``3.1``, ``3.2``, ``4.1`` down a narrow left column. A table
carries ``(1)`` after a column head and prints ``(1) Services net sales
include...`` under the rule. A paragraph enumerates ``(1) online retailers;
(2) publishers; (3) web search engines``. Every one of these was published as a
value, and a parenthesised one was published as a *negative* number, which is
the worst failure the detector can make: linking it puts a wrong figure in a
cell.

None of them can be refused by looking at the token. ``(1)`` in a tax schedule
is a real loss of one, printed identically. What separates the two is that a
marker belongs to a **chain**: a run of ordinals that step through a list in
order, each one leading something. This module finds those chains, and the shape
of the chain is what tells the two apparatuses apart:

* An ordinal that opens a list item is a `list-marker`.
* A *parenthesised* one is `footnote-marker`, because that is the convention
  footnote apparatus uses; the ordinals under the rule of a table are written
  ``(1)``, an ordered list is written ``1.`` or ``3.1``.
* A `footnote-reference` is the indicator the definition answers: the same
  printed form again, trailing the text it annotates, near the definitions.

Nothing here is decided by position on the page: a footnote block sits wherever
the text above it ended, and the exhibit column of a 10-K is a body column.

This module classifies text and line geometry only. The detector owns the
conversion to bounds and the fs-values-v1 envelope.
"""
from __future__ import annotations

import re
from dataclasses import dataclass

from .headings import Fragment, HeadingLine
from .reasons import FOOTNOTE_MARKER, FOOTNOTE_REFERENCE, LIST_MARKER


# ``3.1``, ``10.15``, ``1.``, ``(7)``, ``10.5†``. Roman and alphabetic markers
# are left out deliberately: they are never read as values, so refusing them
# would cost work and publish nothing.
_MARKER = re.compile(
    r"(?P<open>\()?"
    r"(?P<ordinal>\d{1,3}(?:\.\d{1,3})*)"
    r"(?P<close>[).])?"
    r"(?P<mark>[*†‡§¶]{0,2})"
    r"(?=[\s(]|$)"
)
_WORD = re.compile(r"[^\W\d_]{2,}")
# An ordinal that keeps going into a quantity was never a marker: "(5) %" is
# negative five percent, "(1) million" is a magnitude, "(3) 4" is two cells.
_CONTINUES = re.compile(
    r"\s*(?:%|percent\b|\d|thousand|million|billion|trillion)", re.I
)
# Somewhere an inline enumerator may start: after a word, or after the
# punctuation that ends the clause before it.
_ENUMERATOR_AFTER = frozenset(")]”’;:")
# An indicator annotates a word, so only a word -- or the indicator before it,
# as in "(2)(4)(5)" -- may precede one.
_INDICATOR_AFTER = frozenset(")]”’")

# A chain has to step through enough of a list to be one. Two ordinals that
# happen to ascend are a coincidence; three that step by one are a list.
MIN_CHAIN = 3
# How far apart two members of the same column may sit horizontally. Exhibit
# numbers are set ragged inside their column, so this is not zero.
COLUMN_TOLERANCE = 0.012
# An enumeration runs through one passage, not across a page.
INLINE_REACH = 8
# How far ahead of its definitions an indicator may be printed. A table's
# footnotes follow it immediately; an exhibit index carries its notes at the end.
REFERENCE_REACH = 4
# What a marker has to be leading before it counts as leading anything.
MIN_PROSE_WORDS = 2
# How many lines either side of a marker cell its item may be delivered in. The
# cells of one band arrive together, so this only has to survive a stray line
# landing between them.
BAND_REACH = 4


@dataclass(frozen=True)
class Marker:
    """One ordinal the detector must refuse, and the rule that refused it."""

    fragment: Fragment
    text: str
    reason: str


@dataclass(frozen=True)
class ListDetection:
    markers: tuple[Marker, ...]


@dataclass(frozen=True)
class _Candidate:
    """One ordinal marker, placed in the line and the page that printed it."""

    line_index: int
    page_index: int
    start: int
    end: int
    text: str
    ordinal: tuple[int, ...]
    left: float
    parenthesised: bool

    def fragment(self) -> Fragment:
        return Fragment(self.line_index, self.start, self.end)


def _parse(
    line_index: int, line: HeadingLine, at: int
) -> _Candidate | None:
    """The marker beginning at `at`, or None when nothing there is one."""
    match = _MARKER.match(line.text, at)
    if match is None:
        return None
    # A bracket has to close, and a closing bracket has to have opened.
    if bool(match.group("open")) != (match.group("close") == ")"):
        return None
    if _CONTINUES.match(line.text, match.end()):
        return None
    return _Candidate(
        line_index=line_index,
        page_index=line.page_index,
        start=at,
        end=match.end(),
        text=match.group(0),
        ordinal=tuple(int(part) for part in match.group("ordinal").split(".")),
        left=line.left,
        parenthesised=bool(match.group("open")),
    )


def _is_prose(text: str) -> bool:
    """Whether this reads as something a marker introduces, not another cell."""
    return len(_WORD.findall(text)) >= MIN_PROSE_WORDS


def _leads_prose(lines: list[HeadingLine], index: int, rest: str) -> bool:
    """Whether the marker at the head of this line introduces text.

    A marker either keeps its item on the same line, or sits alone in a narrow
    cell with the item beside it -- which is how extracted geometry delivers an
    exhibit index or a footnote block. The second case is the one that matters:
    it is also the shape of a numeric column with another column beside it, and
    requiring *prose* to the right is what separates the two. A column of ages
    beside a column of job titles is the trap, and it is only a trap until you
    ask what the number is leading.
    """
    if rest.strip():
        return _is_prose(rest)
    line = lines[index]
    for other in lines[max(0, index - BAND_REACH):index + BAND_REACH + 1]:
        if other is line or other.page_index != line.page_index:
            continue
        if other.top >= line.bottom or other.bottom <= line.top:
            continue
        if other.left > line.right + line.median_width and _is_prose(other.text):
            return True
    return False


def _candidates(
    lines: list[HeadingLine],
) -> tuple[list[_Candidate], list[_Candidate], list[_Candidate]]:
    """Every ordinal marker, split by where in its line it sits.

    Leading markers open a list item, inline ones enumerate through a sentence,
    and trailing ones annotate what comes before them. The three are exclusive,
    and what decides between the last two is what follows the marker: an
    enumerator is followed by the item it introduces, an indicator by nothing
    but the next indicator.
    """
    leading: list[_Candidate] = []
    inline: list[_Candidate] = []
    trailing: list[_Candidate] = []
    for index, line in enumerate(lines):
        text = line.text
        head = len(text) - len(text.lstrip())
        candidate = _parse(index, line, head)
        if candidate is not None and _leads_prose(lines, index, text[candidate.end:]):
            leading.append(candidate)
        for opening in re.finditer(r"(?<=\S)\s?(?=\(?\d)", text):
            at = opening.end()
            if at <= head:
                continue
            candidate = _parse(index, line, at)
            if candidate is None:
                continue
            # What the marker attaches to, with sentence punctuation set aside so
            # "Inc. (20)" reads as an annotated word. A figure's last digit
            # survives the trim, which is what keeps "1,234 (56)" and
            # "Article 5(4)" from being read as anything but the figures they are.
            before = text[:at].rstrip().rstrip(".,")[-1:]
            rest = text[candidate.end:].strip()
            if (not rest or _MARKER.match(rest)) and (
                before.isalpha() or before in _INDICATOR_AFTER
            ):
                trailing.append(candidate)
            elif _is_prose(rest) and (before.isalpha() or before in _ENUMERATOR_AFTER):
                inline.append(candidate)
    return leading, inline, trailing


def _follows(first: tuple[int, ...], second: tuple[int, ...]) -> bool:
    """Whether `second` is the ordinal a list prints after `first`.

    Either the last part advances by one inside the same group -- ``3.1`` to
    ``3.2``, ``(1)`` to ``(2)`` -- or a new group opens and its numbering
    restarts at one, which is how ``4.4`` is followed by ``10.1``. Everything
    else is two numbers that happen to ascend, and that is what a column of
    quantities, ages or model numbers does.
    """
    if second <= first:
        return False
    if len(first) == len(second) and first[:-1] == second[:-1]:
        return second[-1] == first[-1] + 1
    return second[-1] == 1


def _chains(
    candidates: list[_Candidate],
    adjacent,
) -> list[list[_Candidate]]:
    """Maximal runs of ordinals that follow one another and stay together."""
    found: list[list[_Candidate]] = []
    current: list[_Candidate] = []
    for candidate in sorted(candidates, key=lambda item: (item.line_index, item.start)):
        if current and not (
            _follows(current[-1].ordinal, candidate.ordinal)
            and adjacent(current[-1], candidate)
        ):
            if len(current) >= MIN_CHAIN:
                found.append(current)
            current = []
        current.append(candidate)
    if len(current) >= MIN_CHAIN:
        found.append(current)
    return found


def detect_lists(lines: list[HeadingLine]) -> ListDetection:
    """Find the ordinals that number a list, a footnote, or a reference to one."""
    leading, inline, trailing = _candidates(lines)
    markers: list[Marker] = []
    footnotes: list[tuple[frozenset[str], int, int]] = []

    def same_column(first: _Candidate, second: _Candidate) -> bool:
        tolerance = max(COLUMN_TOLERANCE, lines[second.line_index].median_width * 3)
        return (
            abs(first.left - second.left) <= tolerance
            and 0 <= second.page_index - first.page_index <= 1
        )

    def same_passage(first: _Candidate, second: _Candidate) -> bool:
        return (
            first.page_index == second.page_index
            and second.line_index - first.line_index <= INLINE_REACH
        )

    for parenthesised in (False, True):
        column = [item for item in leading if item.parenthesised == parenthesised]
        for chain in _chains(column, same_column):
            reason = FOOTNOTE_MARKER if parenthesised else LIST_MARKER
            markers.extend(
                Marker(item.fragment(), item.text, reason) for item in chain
            )
            if parenthesised:
                footnotes.append(
                    (
                        frozenset(item.text for item in chain),
                        chain[0].page_index,
                        chain[-1].page_index,
                    )
                )

    for chain in _chains(inline, same_passage):
        markers.extend(Marker(item.fragment(), item.text, LIST_MARKER) for item in chain)

    claimed = {(marker.fragment.line_index, marker.fragment.start) for marker in markers}
    for candidate in trailing:
        if (candidate.line_index, candidate.start) in claimed:
            continue
        if any(
            candidate.text in forms and first - REFERENCE_REACH <= candidate.page_index <= last
            for forms, first, last in footnotes
        ):
            markers.append(
                Marker(candidate.fragment(), candidate.text, FOOTNOTE_REFERENCE)
            )

    return ListDetection(markers=tuple(markers))
