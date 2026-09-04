"""Fit a column/row grid to a proposed region and assemble logical rows.

Fitting is iterative because the two halves depend on each other: columns are
inferred from the lines that look like rows, and which lines are rows depends on
where the columns are. Three passes are enough in practice — the second pass
exists mainly so that a wrapped description line stops voting on column
positions once it has been recognized as a continuation.
"""
from __future__ import annotations

import math
import re

from engines.table.layout import PageLayout, LogicalRow, TextToken, VisualLine
from engines.table.rulings import RulingSegment


# A boundary must be free of text on this share of the rows that could contradict
# it. One row may legitimately overflow its cell; systematic crossing means the
# boundary is imaginary.
_MAX_CROSSING = 0.15
# How much crossing the first, exploratory vote tolerates while it locates the
# corridors that later passes judge.
_SPANNING_CROSSING = 0.40
_MIN_SUPPORT = 0.35
_ITERATIONS = 3

# Accounting layouts float a currency or sign marker in its own whitespace island,
# well clear of the amount it belongs to. It is a marker, never a column.
_MARKER_ONLY = re.compile(r"^[$€£¥%()\[\]*†‡]+$")


class GridHypothesis:
    """Columns, logical rows and the cell text they imply."""

    def __init__(
        self,
        columns: list[dict],
        rows: list[LogicalRow],
        boundaries: list[float],
        *,
        ruled_columns: bool = False,
        ruled_rows: bool = False,
    ) -> None:
        self.columns = columns
        self.rows = rows
        self.boundaries = boundaries
        self.ruled_columns = ruled_columns
        self.ruled_rows = ruled_rows

    @property
    def column_count(self) -> int:
        return len(self.columns)

    def cell_matrix(self) -> list[list[str]]:
        return [row.cells for row in self.rows]


def column_index(boundaries: list[float], value: float) -> int:
    index = 0
    for boundary in boundaries:
        if value < boundary:
            return index
        index += 1
    return index


def assign_tokens(line: VisualLine, boundaries: list[float]) -> list[list[TextToken]]:
    """Place every word of a line into a column, by the word's centre."""
    buckets: list[list[TextToken]] = [[] for _ in range(len(boundaries) + 1)]
    for token in line.tokens:
        buckets[column_index(boundaries, token.center)].append(token)
    # An accounting layout floats the currency symbol in its own whitespace island
    # to the left of the amount it belongs to, which lands it at the tail of the
    # previous column. A percent sign is the mirror image: it trails its number
    # closely enough to be swept into the next column. Both are re-attached to the
    # value they qualify, so no cell reads "% $ 201,183".
    for index in range(len(buckets) - 1):
        while (
            buckets[index]
            and buckets[index][-1].kind == "currency"
            and buckets[index + 1]
            and buckets[index + 1][0].is_value
        ):
            buckets[index + 1].insert(0, buckets[index].pop())
        while (
            buckets[index + 1]
            and buckets[index + 1][0].text in ("%", ")")
            and buckets[index]
            and buckets[index][-1].is_value
        ):
            buckets[index].append(buckets[index + 1].pop(0))
    return buckets


def cells_for(line: VisualLine, boundaries: list[float]) -> list[str]:
    return [
        " ".join(token.text for token in bucket).strip()
        for bucket in assign_tokens(line, boundaries)
    ]


def occupied_columns(line: VisualLine, boundaries: list[float]) -> tuple[int, ...]:
    """Which columns hold real content — markers alone do not count as content."""
    buckets = assign_tokens(line, boundaries)
    return tuple(
        index
        for index, bucket in enumerate(buckets)
        if any(not token.is_marker for token in bucket)
    )


def _coverage(lines: list[VisualLine], left: float, right: float, step: float):
    count = max(1, int(math.ceil((right - left) / step)))
    profiles = []
    for line in lines:
        covered = bytearray(count)
        for token in line.tokens:
            start = max(0, int((token.x0 - left) / step))
            end = min(count - 1, int((token.x1 - left) / step))
            for index in range(start, end + 1):
                covered[index] = 1
        interior_start = min(token.x0 for token in line.tokens)
        interior_end = max(token.x1 for token in line.tokens)
        profiles.append((covered, interior_start, interior_end))
    return count, profiles


def _vote(
    anchors: list[VisualLine], left: float, right: float, layout: PageLayout, crossing: float
) -> list[float]:
    step = max(0.0015, (right - left) / 400)
    if len(anchors) < 2 or right <= left:
        return []
    count, profiles = _coverage(anchors, left, right, step)
    # A row of amounts is stronger evidence about where the columns are than a
    # line of column titles: titles are centred, wrapped and often span two
    # columns, while values sit squarely in theirs. Weighting the vote is what
    # lets a four-line stacked header sit above the body without dictating it.
    weights = [
        2.0 if any(token.kind in ("numeric", "percent") for token in line.tokens) else 1.0
        for line in anchors
    ]
    total_weight = sum(weights)
    required = max(2.0, total_weight * _MIN_SUPPORT)
    allowed = total_weight * crossing

    support = [0.0] * count
    against = [0.0] * count
    for weight, (covered, interior_start, interior_end) in zip(weights, profiles):
        for index in range(count):
            position = left + (index + 0.5) * step
            if covered[index]:
                against[index] += weight
            elif interior_start < position < interior_end:
                support[index] += weight

    runs: list[tuple[int, int]] = []
    start = None
    for index in range(count):
        good = support[index] >= required and against[index] <= allowed
        if good and start is None:
            start = index
        elif not good and start is not None:
            runs.append((start, index - 1))
            start = None
    if start is not None:
        runs.append((start, count - 1))

    boundaries: list[float] = []
    for run_start, run_end in runs:
        width = (run_end - run_start + 1) * step
        if width < layout.min_gutter * 0.5:
            continue
        # Weight the centre by how many rows support each sample, so a corridor
        # that is wide on one row and narrow on twenty settles on the twenty.
        weights = [support[index] for index in range(run_start, run_end + 1)]
        total = sum(weights) or 1
        centre = sum(
            (left + (index + 0.5) * step) * weight
            for index, weight in zip(range(run_start, run_end + 1), weights)
        ) / total
        boundaries.append(centre)
    return boundaries


def _spanning(line: VisualLine, boundaries: list[float]) -> bool:
    """Does this line lie across the columns rather than inside them?

    A header band or a spanning caption covers several columns with one word and
    fills fewer islands than there are columns. An ordinary row that happens to
    overflow one cell fails the second test, so it keeps its vote — and its veto.
    """
    crossed = sum(
        1
        for boundary in boundaries
        if any(token.x0 < boundary < token.x1 for token in line.tokens)
    )
    return crossed >= 2 and line.segment_count <= len(boundaries)


def infer_boundaries(
    lines: list[VisualLine], left: float, right: float, layout: PageLayout
) -> list[float]:
    """Column boundaries from whitespace corridors shared by many rows.

    A position supports a boundary when a row has text on both sides of it and no
    text on it. Positions beyond a short row's last word are neither support nor
    contradiction: a row that simply stops early says nothing about where the
    columns are.

    The vote is taken twice. A header band or a spanning caption sits across
    several columns at once, so on the first pass it looks like evidence against
    every boundary it covers; the second pass removes those lines from the vote
    entirely, which lets an ordinary row veto a boundary that cuts through its
    text while a header cannot veto the columns it spans.
    """
    anchors = [line for line in lines if line.segment_count >= 2 and line.tokens]
    if len(anchors) < 2 or right <= left:
        return []
    # The first pass is deliberately tolerant of text crossing a boundary: its job
    # is only to locate the corridors, and a two-line stacked header would
    # otherwise veto the very columns it labels before it can be recognized.
    proposed = _vote(anchors, left, right, layout, _SPANNING_CROSSING)
    if not proposed:
        return []
    ordinary = [line for line in anchors if not _spanning(line, proposed)]
    if len(ordinary) >= 2 and len(ordinary) < len(anchors):
        settled = _vote(ordinary, left, right, layout, _MAX_CROSSING)
        if settled:
            return settled
    # No spanning line to remove, or removing them left nothing: fall back to the
    # strict vote over every anchor, and to the tolerant proposal only if that
    # finds no columns at all.
    return _vote(anchors, left, right, layout, _MAX_CROSSING) or proposed


def _coalesce(
    boundaries: list[float], lines: list[VisualLine], left: float, right: float
) -> list[float]:
    """Remove boundaries that create columns holding no data of their own.

    Every removal drops a boundary, never a cell, so each word stays inside some
    column. Over-segmentation here is what produced empty spreadsheet columns and
    split a currency symbol away from the amount it belongs to.

    Three shapes are collapsed: a band no row fills; a band only the column titles
    fill, which is what a wide corridor's centre landing inside a header label
    produces; and a band holding nothing but a floated currency or sign marker,
    which belongs to the amount beside it.
    """
    anchors = [line for line in lines if line.segment_count >= 2 and line.tokens]
    if not anchors:
        return boundaries
    while boundaries:
        matrix = [cells_for(line, boundaries) for line in anchors]
        # A row that carries an amount is a body row; a row of words alone is a
        # column title. A column only titles fill carries no data.
        body_rows = [
            index
            for index, line in enumerate(anchors)
            if any(token.kind in ("numeric", "percent") for token in line.tokens)
        ]
        removed = False
        for index in range(len(boundaries) + 1):
            column = [row[index] for row in matrix if index < len(row)]
            filled = [row for row, value in enumerate(column) if value]
            values = [column[row] for row in filled]
            dead = not filled or (
                bool(body_rows) and not any(row in body_rows for row in filled)
            )
            marker_only = (
                bool(values)
                and index < len(boundaries)
                and all(_MARKER_ONLY.match(value) for value in values)
            )
            if not dead and not marker_only:
                continue
            if marker_only and not dead:
                # Fold the marker into the column on its right, where its amount is.
                boundaries = boundaries[:index] + boundaries[index + 1 :]
            elif index == 0:
                boundaries = boundaries[1:]
            elif index == len(boundaries):
                boundaries = boundaries[:-1]
            else:
                boundaries = boundaries[: index - 1] + boundaries[index:]
            removed = True
            break
        if not removed:
            break
    return boundaries


def ruled_boundaries(
    vertical: list[RulingSegment], bounds: dict
) -> list[float]:
    """Interior vertical rules that run down most of the region."""
    top = bounds["y"]
    bottom = top + bounds["height"]
    height = max(1e-6, bottom - top)
    inside = [
        rule
        for rule in vertical
        if bounds["x"] - 0.004 <= rule.position <= bounds["x"] + bounds["width"] + 0.004
        and (min(rule.end, bottom) - max(rule.start, top)) / height >= 0.55
    ]
    positions = sorted(rule.position for rule in inside)
    if len(positions) < 3:
        return []
    return positions[1:-1]


def _is_continuation(
    line: VisualLine,
    previous: LogicalRow,
    boundaries: list[float],
    layout: PageLayout,
) -> bool:
    """Is this line the tail of the row above rather than a row of its own?

    A wrapped cell holds content in exactly one column, that column already holds
    content in the row above, the line is indented no further left than the cell
    it continues, and it follows immediately. A section label such as
    "Deferred tax assets:" fails the last two tests, which is what keeps it a row.
    """
    # A line the layout marked as prose only reached this band because it is the
    # first line of a wrapped row label, whose amounts are on the line below. It
    # opens a row; it never closes the one above.
    if layout.is_prose(line):
        return False
    occupied = occupied_columns(line, boundaries)
    if len(occupied) != 1 or not previous.occupied:
        return False
    column = occupied[0]
    if column not in previous.occupied or len(previous.occupied) < 2:
        return False
    if line.y0 - previous.y1 > layout.line_height * 0.9:
        return False
    text = line.text.strip()
    if text.endswith(":"):
        return False
    meaningful = [token for token in line.tokens if not token.is_marker]
    if (
        meaningful
        and all(token.is_value for token in meaningful)
        and column < len(previous.cells)
        and previous.cells[column]
    ):
        # A cell wraps its words, not its amounts. A period band ("2024") landing
        # directly under a filled value column is the start of the next table, not
        # the second line of the number above it.
        return False
    previous_start = min(
        (token.x0 for token in previous.anchor.tokens if column_index(boundaries, token.center) == column),
        default=line.x0,
    )
    return line.x0 >= previous_start - layout.character_width


def _absorbs_wrapped_label(
    previous: LogicalRow,
    line: VisualLine,
    boundaries: list[float],
    layout: PageLayout,
) -> bool:
    """Does this row finish a label that began on the line above?

    The mirror of `_is_continuation`: there the wrapped text follows its values,
    here it precedes them. A label long enough to fill the body width and holding
    nothing else is not a row of its own — the amounts belong to it. A section
    caption ("Deferred tax assets:") is short and punctuated, so it stays a row.
    """
    # An unfinished row: text in the first column and nothing anywhere else,
    # because its amounts are on the line below — which does reach the last
    # column. A row that already carries values of its own is complete.
    if len(previous.lines) > 1 or previous.occupied != (0,):
        return False
    if len(boundaries) not in occupied_columns(line, boundaries):
        return False
    label = previous.anchor
    if label.text.strip().endswith(":") or label.width < layout.body_width * 0.45:
        return False
    return line.y0 - previous.y1 <= layout.line_height * 0.9


def build_logical_rows(
    lines: list[VisualLine], boundaries: list[float], layout: PageLayout
) -> list[LogicalRow]:
    def refresh(row: LogicalRow) -> None:
        row.cells = _row_cells(row, boundaries)
        row.occupied = tuple(
            index for index, value in enumerate(row.cells) if value and not _marker_only(value)
        )

    rows: list[LogicalRow] = []
    for line in lines:
        if not line.tokens:
            continue
        previous = rows[-1] if rows else None
        if previous is not None and (
            _is_continuation(line, previous, boundaries, layout)
            or _absorbs_wrapped_label(previous, line, boundaries, layout)
        ):
            previous.lines.append(line)
            refresh(previous)
            continue
        row = LogicalRow(lines=[line])
        refresh(row)
        rows.append(row)
    for row in rows:
        if row.occupied == (0,):
            row.kind = "section"
    return rows


def _marker_only(value: str) -> bool:
    return all(character in "$€£¥%()[]*†‡§# " for character in value)


def _row_cells(row: LogicalRow, boundaries: list[float]) -> list[str]:
    cells = [""] * (len(boundaries) + 1)
    for line in row.lines:
        for index, bucket in enumerate(assign_tokens(line, boundaries)):
            text = " ".join(token.text for token in bucket).strip()
            if not text:
                continue
            cells[index] = f"{cells[index]} {text}".strip() if cells[index] else text
    return cells


def _row_bands(rows: list[LogicalRow], top: float, bottom: float) -> list[tuple[float, float]]:
    centers = [(row.y0 + row.y1) / 2 for row in rows]
    edges = [top]
    for index in range(len(centers) - 1):
        edges.append((centers[index] + centers[index + 1]) / 2)
    edges.append(bottom)
    return [(edges[index], edges[index + 1]) for index in range(len(rows))]


def ruled_row_edges(
    horizontal: list[RulingSegment], bounds: dict, *, minimum_span: float = 0.6
) -> list[float]:
    """Horizontal rules that band the whole region, not just underline a subtotal."""
    left = bounds["x"]
    width = max(1e-6, bounds["width"])
    top = bounds["y"]
    bottom = top + bounds["height"]
    spanning = [
        rule.position
        for rule in horizontal
        if top - 0.004 <= rule.position <= bottom + 0.004
        and (min(rule.end, left + width) - max(rule.start, left)) / width >= minimum_span
    ]
    return sorted(spanning)


def fit_grid(candidate, layout: PageLayout) -> GridHypothesis | None:
    """Iterate columns and logical rows until both stop changing."""
    lines = [line for line in candidate.lines if line.tokens]
    if len(lines) < 2:
        return None
    left, right = candidate.left, candidate.right
    ruled = ruled_boundaries(candidate.vertical, candidate.bounds)
    boundaries: list[float] = []
    rows: list[LogicalRow] = []
    voting = lines
    for _ in range(_ITERATIONS):
        if ruled:
            new_boundaries = list(ruled)
        else:
            new_boundaries = infer_boundaries(voting, left, right, layout)
            new_boundaries = _coalesce(new_boundaries, voting, left, right)
        if not new_boundaries:
            return None
        rows = build_logical_rows(lines, new_boundaries, layout)
        settled = len(new_boundaries) == len(boundaries) and all(
            abs(first - second) < 1e-4 for first, second in zip(new_boundaries, boundaries)
        )
        boundaries = new_boundaries
        # Continuations are wrapped text, not evidence about column positions, so
        # the next vote is taken without them.
        voting = [row.anchor for row in rows if row.anchor.segment_count >= 2]
        if settled or ruled:
            break
    if not boundaries or len(rows) < 2:
        return None

    edges = [left] + sorted(boundaries) + [right]
    columns = [{"x0": edges[index], "x1": edges[index + 1]} for index in range(len(edges) - 1)]
    return GridHypothesis(
        columns=columns,
        rows=rows,
        boundaries=sorted(boundaries),
        ruled_columns=bool(ruled),
        ruled_rows=False,
    )
