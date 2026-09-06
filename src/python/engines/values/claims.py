"""A claim another engine makes on a span of text.

The value tier reads every line of a document, and some of what it reads has
already been understood by a tier above it: a note heading, a contents row, a
citation. Rather than have this engine import that one -- the dependency runs
the other way -- the tier above hands down claims, and this engine honours them.

A claim is not a value and never becomes one. It either replaces what the value
tier would have published, or it fences off a range so nothing is published from
inside it.
"""
from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class ClaimedSpan:
    """One claimed range, and what the value tier must do with it.

    A `REFERENCE` claim is published whole, under `identifier`, and nothing
    inside it is recognized separately -- the citation is the thing, not the
    digit in it. A `NOISE` claim is not published whole: it fences a range, and
    any value-shaped text found inside is published as noise carrying `label`.
    That is the difference between a heading, which is structure and belongs to
    the model above, and the integer printed inside it, which is a refusal and
    belongs here.
    """

    line_index: int
    start: int
    end: int
    category: str
    label: str
    text: str
    bounds: dict
    identifier: str = ""
