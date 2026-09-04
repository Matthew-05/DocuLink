"""Propose table regions from ruled, whitespace and mixed evidence.

A generator only says "something table-shaped may live here". It never decides
that the region is a table — that is the job of grid fitting, refinement and
scoring downstream. Keeping proposal cheap and generous is deliberate: recall is
recoverable later, but a region that was never proposed can never be found.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from statistics import median

from engines.table.layout import PageLayout, VisualLine
from engines.table.rulings import PageRulings, RulingSegment, ruling_components


# How much of a candidate's height a vertical rule must cover before it is
# trusted as a column boundary rather than treated as a stray box edge.
_RULE_COVERAGE = 0.55

# How far outside the anchored columns a caption may start, as a fraction of the
# page, and how wide it may be relative to them. A section label is outdented by
# about one indent step and is a label, not a sentence.
_CAPTION_REACH = 0.03
_CAPTION_WIDTH = 0.6
# How far into the table a line must begin before it reads as a band floated over
# the value columns rather than as a line of the page's body text.
_CAPTION_FLOAT = 0.25


@dataclass
class TableCandidate:
    """A proposed region, its supporting lines and the rules that back it."""

    lines: list[VisualLine]
    bounds: dict
    evidence: str  # "ruled" | "whitespace" | "mixed"
    origin: str = "whitespace"
    vertical: list[RulingSegment] = field(default_factory=list)
    horizontal: list[RulingSegment] = field(default_factory=list)
    graphics: list[tuple[float, float, float, float]] = field(default_factory=list)
    rejection: str | None = None
    # A candidate produced by splitting a larger one. Refinement decided these
    # rows are their own table, so merging must never put them back together.
    sealed: bool = False

    @property
    def anchors(self) -> list[VisualLine]:
        return [line for line in self.lines if line.segment_count >= 2]

    @property
    def left(self) -> float:
        return self.bounds["x"]

    @property
    def right(self) -> float:
        return self.bounds["x"] + self.bounds["width"]

    @property
    def top(self) -> float:
        return self.bounds["y"]

    @property
    def bottom(self) -> float:
        return self.bounds["y"] + self.bounds["height"]


def make_bounds(x0: float, y0: float, x1: float, y1: float) -> dict:
    x0 = max(0.0, min(1.0, x0))
    y0 = max(0.0, min(1.0, y0))
    x1 = max(0.0, min(1.0, x1))
    y1 = max(0.0, min(1.0, y1))
    return {"x": x0, "y": y0, "width": max(1e-6, x1 - x0), "height": max(1e-6, y1 - y0)}


def overlap_area(first: dict, second: dict) -> float:
    left = max(first["x"], second["x"])
    top = max(first["y"], second["y"])
    right = min(first["x"] + first["width"], second["x"] + second["width"])
    bottom = min(first["y"] + first["height"], second["y"] + second["height"])
    return max(0.0, right - left) * max(0.0, bottom - top)


def intersection_over_union(first: dict, second: dict) -> float:
    intersection = overlap_area(first, second)
    union = (
        first["width"] * first["height"] + second["width"] * second["height"] - intersection
    )
    return intersection / union if union > 0 else 0.0


def containment(inner: dict, outer: dict) -> float:
    """How much of `inner` lies inside `outer`."""
    area = inner["width"] * inner["height"]
    return overlap_area(inner, outer) / area if area > 0 else 0.0


def supported_edge(lines: list[VisualLine], key: str, tolerance: float) -> float:
    """Keep repeated outdents without letting one stray glyph widen a table.

    A genuine edge normally recurs — section starts, totals, an aligned value
    column. The outermost edge that at least two lines share wins; a lone OCR
    speck near the page margin is ignored.
    """
    rightmost = key == "x1"
    values = sorted((getattr(line, key) for line in lines), reverse=rightmost)
    if not values:
        return 0.0
    if len(values) < 2:
        return values[0]
    for value in values:
        peers = [other for other in values if abs(other - value) <= tolerance]
        if len(peers) >= 2:
            return median(peers)
    return median(values)


def _wraps_into_a_row(layout: PageLayout, index: int) -> bool:
    """Is this full-width line the first line of a long row label?

    A balance sheet writes "Common stock and additional paid-in capital, $0.00001
    par value: 50,400,000 shares authorized; 14,773,260" on one line and "and
    15,116,786 shares issued and outstanding, respectively   93,568   83,276" on
    the next. The first line reaches the body margin and carries no values, so it
    reads exactly like a paragraph — until you notice that the line below it is a
    data row that finishes the sentence. A paragraph is never followed by amounts
    laid out in columns.
    """
    if index + 1 >= len(layout.lines):
        return False
    line = layout.lines[index]
    following = layout.lines[index + 1]
    if following.segment_count < 2 or layout.is_prose(following):
        return False
    if following.y0 - line.y1 > layout.line_height * 0.8:
        return False
    return (
        sum(1 for token in following.tokens if token.kind in ("numeric", "percent")) >= 2
    )


def _bands(layout: PageLayout) -> list[list[VisualLine]]:
    """Split the page into runs of lines that could belong to one block.

    Two things end a run: a blank band taller than the normal line pitch, and a
    line of running prose — which includes the short line a paragraph ends on, or
    the tail of "…was as follows (in millions):" would head the table it
    introduces. Everything else — section captions, wrapped cells, sparse period
    headers — stays inside the run, because deciding what those are needs columns,
    which do not exist yet.
    """
    bands: list[list[VisualLine]] = []
    current: list[VisualLine] = []
    previous: VisualLine | None = None
    for index, line in enumerate(layout.lines):
        if layout.is_prose(line) and not _wraps_into_a_row(layout, index):
            if current:
                bands.append(current)
            current = []
            previous = line
            continue
        if previous is not None and line.y0 - previous.y1 > layout.row_gap:
            if current:
                bands.append(current)
            current = []
        current.append(line)
        previous = line
    if current:
        bands.append(current)
    return bands


def whitespace_candidates(layout: PageLayout) -> list[TableCandidate]:
    """Alignment-anchored proposals: runs of lines that share whitespace corridors."""
    proposals: list[TableCandidate] = []
    tolerance = max(0.003, layout.character_width * 1.25)
    for band in _bands(layout):
        positions = [
            index for index, line in enumerate(band) if line.segment_count >= 2
        ]
        if len(positions) < 2:
            continue
        anchors = [band[index] for index in positions]
        first, last = positions[0], positions[-1]
        left = supported_edge(anchors, "x0", tolerance)
        right = supported_edge(anchors, "x1", tolerance)
        # Reach up for the caption directly above the anchored run — a period
        # super-header is frequently the only header a statement has — but admit
        # only what reads as a label: close above, no wider than a fraction of the
        # table, and no further left than one indent step outside it. A sentence
        # introducing the table is none of those things.
        span = max(1e-6, right - left)
        lead = first
        while lead > 0:
            above = band[lead - 1]
            if band[first].y0 - above.y1 > layout.line_height * 1.2:
                break
            # A label is short. A period band spanning the value columns is not,
            # but it starts well inside the table rather than at the margin the
            # page sets its sentences from, which is what separates the two.
            floated = above.x0 >= left + span * _CAPTION_FLOAT
            if not floated and above.width > span * _CAPTION_WIDTH:
                break
            if above.x0 < left - _CAPTION_REACH:
                break
            lead -= 1
        lines = band[lead : last + 1]
        left = min([left] + [line.x0 for line in lines if line.x0 >= left - 0.04])
        right = max([right] + [line.x1 for line in lines if line.x1 <= right + 0.04])
        proposals.append(
            TableCandidate(
                lines=list(lines),
                bounds=make_bounds(
                    left - layout.character_width * 0.5,
                    lines[0].y0 - layout.line_height * 0.4,
                    right + layout.character_width * 0.5,
                    lines[-1].y1 + layout.line_height * 0.4,
                ),
                evidence="whitespace",
                origin="alignment",
            )
        )
    return proposals


def ruled_candidates(layout: PageLayout, rulings: PageRulings) -> list[TableCandidate]:
    """One proposal per connected group of intersecting rules."""
    proposals: list[TableCandidate] = []
    for vertical, horizontal in ruling_components(rulings):
        if len(vertical) < 2 or len(horizontal) < 2:
            continue
        x0 = min([rule.position for rule in vertical] + [rule.start for rule in horizontal])
        x1 = max([rule.position for rule in vertical] + [rule.end for rule in horizontal])
        y0 = min([rule.position for rule in horizontal] + [rule.start for rule in vertical])
        y1 = max([rule.position for rule in horizontal] + [rule.end for rule in vertical])
        bounds = make_bounds(x0, y0, x1, y1)
        lines = [
            line
            for line in layout.lines
            if bounds["y"] - layout.line_height * 0.3
            <= line.center
            <= bounds["y"] + bounds["height"] + layout.line_height * 0.3
            and line.x1 >= bounds["x"]
            and line.x0 <= bounds["x"] + bounds["width"]
        ]
        proposals.append(
            TableCandidate(
                lines=lines,
                bounds=bounds,
                evidence="ruled",
                origin="ruling-component",
                vertical=vertical,
                horizontal=horizontal,
                graphics=list(rulings.graphics),
            )
        )
    return proposals


def _rules_inside(rules: list[RulingSegment], bounds: dict, *, axis: str) -> list[RulingSegment]:
    if axis == "vertical":
        low, high = bounds["x"], bounds["x"] + bounds["width"]
        start, end = bounds["y"], bounds["y"] + bounds["height"]
    else:
        low, high = bounds["y"], bounds["y"] + bounds["height"]
        start, end = bounds["x"], bounds["x"] + bounds["width"]
    kept = []
    for rule in rules:
        if not (low - 0.004 <= rule.position <= high + 0.004):
            continue
        covered = min(rule.end, end) - max(rule.start, start)
        if covered <= 0:
            continue
        kept.append(rule)
    return kept


def combine(
    layout: PageLayout,
    whitespace: list[TableCandidate],
    ruled: list[TableCandidate],
    rulings: PageRulings,
) -> list[TableCandidate]:
    """Let ruled and whitespace evidence reinforce one another.

    Financial statements rule their subtotals horizontally but define columns with
    whitespace alone, so neither pure mode describes them. Where the two agree on
    a region the proposals are folded into one `mixed` candidate that carries both
    kinds of evidence; where only one fires, it stands on its own.
    """
    combined: list[TableCandidate] = []
    consumed: set[int] = set()
    for candidate in whitespace:
        candidate.graphics = list(rulings.graphics)
        for index, grid in enumerate(ruled):
            if index in consumed:
                continue
            if (
                containment(grid.bounds, candidate.bounds) >= 0.6
                or containment(candidate.bounds, grid.bounds) >= 0.6
            ):
                consumed.add(index)
                candidate.evidence = "mixed"
                candidate.vertical = grid.vertical
                candidate.horizontal = grid.horizontal
                candidate.bounds = make_bounds(
                    min(candidate.left, grid.left),
                    min(candidate.top, grid.top),
                    max(candidate.right, grid.right),
                    max(candidate.bottom, grid.bottom),
                )
                candidate.lines = sorted(
                    {line.index: line for line in candidate.lines + grid.lines}.values(),
                    key=lambda line: line.center,
                )
        if candidate.evidence == "whitespace":
            # Even without a full grid, rules that sit inside the region are real
            # evidence: an underlined subtotal band, or a boxed header row.
            inside_h = _rules_inside(rulings.horizontal, candidate.bounds, axis="horizontal")
            inside_v = _rules_inside(rulings.vertical, candidate.bounds, axis="vertical")
            if len(inside_h) >= 2 or len(inside_v) >= 2:
                candidate.evidence = "mixed"
                candidate.vertical = inside_v
                candidate.horizontal = inside_h
        combined.append(candidate)
    for index, grid in enumerate(ruled):
        if index in consumed:
            continue
        combined.append(grid)
    return sorted(combined, key=lambda item: (item.bounds["y"], item.bounds["x"]))


def generate(layout: PageLayout, rulings: PageRulings) -> list[TableCandidate]:
    return combine(layout, whitespace_candidates(layout), ruled_candidates(layout, rulings), rulings)
