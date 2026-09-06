"""Content-addressed span identity.

A span's id is a hash over what it is and where it sits, never over the order it
was found in. Positional ids -- the previous scheme -- renumber every span after
an insertion, so a detector upgrade would orphan any review state a workbook had
attached to them. Hashing the content keeps an id stable across detector
versions, which is what lets the financial-statement suite persist a disposition
against a value and still find it after the engine changes.

The honest limit: this is stable across *detector* versions, not across a
re-OCR. Rebuilt geometry moves bounds, and a moved span is a new id. Re-anchoring
after a geometry rebuild is a separate problem and deliberately not solved here.

The scheme has a second use the suite depends on. Because the id is derived and
not assigned, two engines can arrive at the same id for the same span without
speaking to each other: `engines.fs` names the citation spans it resolved by the
id they will be published under, and `engines.values` publishes them under
exactly that id.
"""
from __future__ import annotations

import hashlib

from .categories import NOISE, REFERENCE, STRUCTURE, VALUE


_PREFIX = {VALUE: "val", REFERENCE: "ref", STRUCTURE: "str", NOISE: "noi"}


def span_id(category: str, page_index: int, text: str, bounds: dict, occurrence: int = 0) -> str:
    """The id for one span, from its category, page, text and place.

    Bounds are rounded to three decimals -- roughly a thousandth of the page,
    finer than any two distinct spans sit apart and coarser than the noise in a
    re-fitted glyph box. `occurrence` disambiguates the physically impossible
    case of two identical spans resolving to one key; the detector supplies it
    only after a collision, so an uncollided id never depends on ordering.
    """
    key = "|".join(
        (
            _PREFIX[category],
            str(page_index),
            " ".join(text.split()),
            f"{float(bounds['x']):.3f}",
            f"{float(bounds['y']):.3f}",
            f"{float(bounds['width']):.3f}",
            f"{float(bounds['height']):.3f}",
            str(occurrence),
        )
    )
    digest = hashlib.sha256(key.encode("utf-8")).hexdigest()[:16]
    return f"{_PREFIX[category]}-{digest}"


class SpanIds:
    """Hands out span ids, resolving the collision case deterministically."""

    def __init__(self) -> None:
        self._taken: set[str] = set()

    def assign(self, category: str, page_index: int, text: str, bounds: dict) -> str:
        occurrence = 0
        while True:
            identifier = span_id(category, page_index, text, bounds, occurrence)
            if identifier not in self._taken:
                self._taken.add(identifier)
                return identifier
            occurrence += 1
