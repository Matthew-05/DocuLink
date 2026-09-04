"""Redesigned table detection: layout -> candidates -> grids -> validation.

The pipeline is deliberately linear and each stage owns one decision:

    normalized layout   what is on the page
    candidate generation where something table-shaped might be
    grid fitting         what its columns and rows are
    refinement           where one table ends and the next begins
    header analysis      which bands are labels
    validation           whether it is a table at all, and how sure we are
"""
from __future__ import annotations

from engines.table.candidates import generate
from engines.table.grid import GridHypothesis, ruled_row_edges
from engines.table.headers import detect_header_cells, period_in
from engines.table.layout import PageLayout, build_page_layout
from engines.table.refine import deduplicate, merge_adjacent, refine
from engines.table.rulings import PageRulings, detect_page_ruling_segments
from engines.table.scoring import CandidateFeatures, evaluate


DETECTOR_VERSION = "table-detector-2"


def _row_bands(grid: GridHypothesis, top: float, bottom: float) -> list[tuple[float, float]]:
    """Split the region into bands, one per logical row.

    The edge goes in the gap between two rows, not half way between their
    centres: a row assembled from three wrapped lines has its centre far above
    its last line, and a centre-based edge cut straight through the text it was
    supposed to contain.
    """
    edges = [top]
    for index in range(len(grid.rows) - 1):
        lower = grid.rows[index].y1
        upper = grid.rows[index + 1].y0
        edges.append((lower + upper) / 2 if upper >= lower else (lower + upper) / 2)
    edges.append(bottom)
    # Keep the bands ordered even where two rows overlap vertically.
    for index in range(1, len(edges)):
        if edges[index] < edges[index - 1]:
            edges[index] = edges[index - 1]
    return [(edges[index], edges[index + 1]) for index in range(len(grid.rows))]


def _snap(edges: list[tuple[float, float]], rules: list[float], reach: float):
    """Pull row band edges onto drawn rules when one sits within a line of them."""
    if not rules:
        return edges
    snapped: list[tuple[float, float]] = []
    for y0, y1 in edges:
        low = min(rules, key=lambda rule: abs(rule - y0))
        high = min(rules, key=lambda rule: abs(rule - y1))
        snapped.append(
            (
                low if abs(low - y0) <= reach else y0,
                high if abs(high - y1) <= reach else y1,
            )
        )
    # Snapping each edge independently can invert a thin band; drop any inversion.
    fixed: list[tuple[float, float]] = []
    for index, (y0, y1) in enumerate(snapped):
        if y1 <= y0:
            fixed.append(edges[index])
        else:
            fixed.append((y0, y1))
    return fixed


def _period(grid: GridHypothesis, header: dict | None) -> dict | None:
    """What span of time the table covers, from its header and its captions.

    A statement dates each column, so the periods come from the header labels. A
    stacked report labels the whole block once above the titles and qualifies the
    dates separately; both of those bands are excluded from the grid, so what
    they said is recorded here rather than lost with them.
    """
    columns = [
        period_in(label) for label in (header or {}).get("labels", [""] * grid.column_count)
    ]
    columns += [""] * (grid.column_count - len(columns))
    table = ""
    qualifier = ""
    for caption in grid.captions:
        if caption["kind"] == "period" and not table:
            table = period_in(caption["text"]) or caption["text"]
        elif caption["kind"] == "qualifier" and not qualifier:
            qualifier = caption["text"]
    if not table and not qualifier and not any(columns):
        return None
    return {"table": table, "columns": columns[: grid.column_count], "qualifier": qualifier}


def _merge_confidence(row, layout: PageLayout) -> float:
    if len(row.lines) < 2:
        return 0.0
    gaps = [
        row.lines[index + 1].y0 - row.lines[index].y1 for index in range(len(row.lines) - 1)
    ]
    proximity = max(0.0, 1.0 - max(0.0, max(gaps)) / max(1e-6, layout.line_height))
    emptiness = (len(row.cells) - 1) / max(1, len(row.cells))
    return round(min(0.99, 0.6 + emptiness * 0.25 + proximity * 0.15), 4)


def detect_page(
    page_geometry: dict,
    page,
    *,
    page_index: int,
    diagnostics: dict | None = None,
    rejected: list[dict] | None = None,
) -> list[dict]:
    """Every table on one page, in reading order.

    Pass `rejected` to collect the candidates that were fitted but not published,
    each with the reason it lost. The overlay renderer draws them; nothing in the
    production path looks at them.
    """
    layout = build_page_layout(page_geometry)
    rulings: PageRulings = (
        detect_page_ruling_segments(page) if page is not None else PageRulings()
    )
    proposals = generate(layout, rulings)
    _count(diagnostics, "table_candidates_generated", len(proposals))

    fitted: list[tuple] = []
    for proposal in proposals:
        parts = refine(proposal, layout)
        if not parts:
            reason = proposal.rejection or "no-grid"
            _reject(diagnostics, reason)
            if rejected is not None:
                rejected.append(
                    {
                        "bounds": proposal.bounds,
                        "evidence": proposal.evidence,
                        "confidence": 0.0,
                        "reason": reason,
                        "columns": [],
                        "rowCount": 0,
                        "features": {},
                    }
                )
        fitted.extend(parts)
    fitted = merge_adjacent(fitted, layout)

    scored: list[dict] = []
    for candidate, grid in fitted:
        matrix = grid.cell_matrix()
        header = detect_header_cells(
            matrix, grid.column_count, ruled=candidate.evidence == "ruled"
        )
        header_rows = int(header["rowCount"]) if header else 0
        features: CandidateFeatures = evaluate(
            candidate, grid, layout, header_rows=header_rows
        )
        confidence = features.confidence()
        if not features.accepted():
            reason = features.weakest()
            _reject(diagnostics, reason)
            if rejected is not None:
                rejected.append(
                    {
                        "bounds": candidate.bounds,
                        "evidence": candidate.evidence,
                        "confidence": confidence,
                        "reason": reason,
                        "columns": [dict(column) for column in grid.columns],
                        "rowCount": len(grid.rows),
                        "features": features.as_dict(),
                    }
                )
            continue
        _count(diagnostics, f"table_evidence_{candidate.evidence}", 1)

        top = candidate.bounds["y"]
        bottom = top + candidate.bounds["height"]
        bands = _row_bands(grid, top, bottom)
        spanning = ruled_row_edges(candidate.horizontal, candidate.bounds)
        bands = _snap(bands, spanning, layout.line_height * 0.5)
        rows_payload = []
        for index, (row, (y0, y1)) in enumerate(zip(grid.rows, bands)):
            rows_payload.append(
                {
                    # Snapping to a drawn rule can nudge an edge past the region;
                    # a row band never reports itself outside its own table.
                    "y0": min(max(top, y0), bottom),
                    "y1": min(max(top, y1), bottom),
                    "kind": "header" if index < header_rows else "body",
                    "textLines": [
                        {"y0": line.y0, "y1": line.y1} for line in row.lines
                    ],
                    "merged": row.merged,
                    "mergeConfidence": _merge_confidence(row, layout),
                }
            )
        scored.append(
            {
                "bounds": candidate.bounds,
                "evidence": candidate.evidence,
                "confidence": confidence,
                "columns": [
                    {"x0": column["x0"], "x1": column["x1"]} for column in grid.columns
                ],
                "rows": rows_payload,
                "header": header,
                "period": _period(grid, header),
                "rulings": {
                    "vertical": sorted(
                        rule.position
                        for rule in candidate.vertical
                        if candidate.bounds["x"] - 0.002
                        <= rule.position
                        <= candidate.bounds["x"] + candidate.bounds["width"] + 0.002
                    ),
                    "horizontal": sorted(
                        rule.position
                        for rule in candidate.horizontal
                        if top - 0.002 <= rule.position <= bottom + 0.002
                    ),
                },
                "features": features.as_dict(),
            }
        )

    tables = deduplicate(scored)
    if rejected is not None:
        kept = {id(table) for table in tables}
        for table in scored:
            if id(table) not in kept:
                rejected.append(
                    {
                        "bounds": table["bounds"],
                        "evidence": table["evidence"],
                        "confidence": table["confidence"],
                        "reason": "overlapping-duplicate",
                        "columns": table["columns"],
                        "rowCount": len(table["rows"]),
                        "features": table.get("features", {}),
                    }
                )
    _count(diagnostics, "table_candidates_emitted", len(tables))
    result = []
    for index, table in enumerate(tables):
        table.pop("features", None)
        result.append({"id": f"page-{page_index}-table-{index}", **table})
    return result


def _count(diagnostics: dict | None, key: str, amount: int) -> None:
    if diagnostics is None:
        return
    diagnostics[key] = diagnostics.get(key, 0) + amount


def _reject(diagnostics: dict | None, reason: str) -> None:
    if diagnostics is None:
        return
    reasons = diagnostics.setdefault("table_rejections", {})
    reasons[reason] = reasons.get(reason, 0) + 1
