"""Find filing item headings, contents rows and narrative references.

An SEC filing indexes itself twice. The body carries headings -- ``Item 1A.
Risk Factors`` -- exactly as a financial statement carries note headings, and
the same heading machinery reads them. The table of contents carries the same
items again, and it is the document's own authority on what each item is
called, so it wins when the two disagree.

A contents row is not one line. In extracted geometry its identifier,
description and page number arrive as separate lines that only share a band, so
rows are assembled geometrically. Where table detection resolved the contents
page, its column bands classify the cells; where it did not -- and on real
filings it sometimes does not -- the same rows are still assembled from the
text alone.

This module classifies text only. The detector owns geometry conversion and the
fs-structure-v1 envelope.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from .headings import (
    Fragment,
    TextLine,
    canonical_description,
    clean_heading_description,
    description_after_reference,
    extend_heading,
    has_separator,
    looks_like_title,
    normalize_roman_identifier,
    trimmed_range,
)


_IDENTIFIER = r"\d{1,3}(?:\.\d{1,2})?[A-Za-z]?"
_HEADER = re.compile(
    rf"^\s*item\s+(?P<identifier>{_IDENTIFIER})\b(?P<rest>.*?)\s*$",
    re.I,
)
_REFERENCE = re.compile(
    rf"\bitems?\s+(?P<identifier>{_IDENTIFIER})\b",
    re.I,
)
_PART = re.compile(
    r"^\s*part\s+(?P<part>[IVXLCDM]{1,6})\b[\s.\-–—:]*(?P<rest>.*?)\s*$",
    re.I,
)
_PART_BEFORE = re.compile(r"\bpart\s+(?P<part>[IVXLCDM]{1,6})\b[\s,.:;\-–—]*$", re.I)
_CONTENTS_TITLE = re.compile(
    r"^\s*(?:table\s+of\s+contents|contents|index(?:\s+to\b.*)?)\s*$",
    re.I,
)
# "Item 601 of Regulation S-K", "Item 408(c) of Regulation S-K". These are
# citations into the rules a filing is written under, not into the filing.
_RULE_CITATION = re.compile(
    r"^(?:\([^()]{1,12}\))*\s*(?:of|under|to)\s+(?:regulation|reg\.|rule|form)\b",
    re.I,
)
# A printed leader run joining a contents entry to its page number.
_LEADERS = re.compile(r"[\s.·•‧∙…_-]*(?:[.·•‧…]\s*){3,}\s*$")
_PAGE_NUMBER = re.compile(r"^[ivxlcdm]{1,7}$|^\d{1,4}$", re.I)

# A contents page must show this many item rows before it is read as one, and
# this share of them must print a page number in a right-hand column. Together
# they are what separates a contents page from a body page that happens to run
# several short item headings together.
MIN_CONTENTS_ROWS = 3
MIN_PAGE_NUMBER_SHARE = 0.5
PAGE_NUMBER_COLUMN_LEFT = 0.6


@dataclass(frozen=True)
class ItemHeader:
    identifier: str
    part: str
    description: str
    continuation: bool
    fragments: tuple[Fragment, ...]


@dataclass(frozen=True)
class ItemTocRow:
    identifier: str
    part: str
    description: str
    printed_page: str
    corroborated: bool
    fragments: tuple[Fragment, ...]


@dataclass(frozen=True)
class ItemReference:
    identifier: str
    part: str
    source_description: str
    fragment: Fragment
    catalog_index: int


@dataclass
class CatalogItem:
    identifier: str
    part: str = ""
    description: str = ""
    description_source: str = "none"
    headers: list[ItemHeader] = field(default_factory=list)
    toc_entries: list[ItemTocRow] = field(default_factory=list)


@dataclass(frozen=True)
class ItemDetection:
    items: tuple[CatalogItem, ...]
    references: tuple[ItemReference, ...]


def _normalize_identifier(raw: str) -> str | None:
    """``01A`` and ``1a`` both become ``1A``; ``2.02`` keeps its printed minor.

    The minor is not renumbered because an 8-K's Item 2.02 and a hypothetical
    Item 2.2 are different items, and the leading zero is how the form
    distinguishes them.
    """
    match = re.fullmatch(r"(\d{1,3})(?:\.(\d{1,2}))?([A-Za-z])?", raw)
    if match is None:
        return None
    major = int(match.group(1))
    if major < 1 or major > 999:
        return None
    identifier = str(major)
    if match.group(2) is not None:
        identifier += f".{match.group(2)}"
    if match.group(3):
        identifier += match.group(3).upper()
    return identifier


def _strip_leaders(text: str) -> str:
    return _LEADERS.sub("", text).strip()


def _parse_part(text: str) -> str | None:
    match = _PART.match(text)
    if match is None:
        return None
    part = normalize_roman_identifier(match.group("part"))
    if part is None:
        return None
    rest = match.group("rest")
    # "Part I, Item 1 of this Form 10-K under..." is prose citing a part, not a
    # part heading. A heading stands alone or names the part.
    if rest.lstrip().startswith(","):
        return None
    if rest and not looks_like_title(_strip_leaders(rest)):
        return None
    return part


def _parse_header(text: str) -> tuple[str, str, bool] | None:
    """(identifier, description, continuation) for a heading line, else None."""
    match = _HEADER.match(text)
    if match is None:
        return None
    identifier = _normalize_identifier(match.group("identifier"))
    if identifier is None:
        return None
    rest = match.group("rest")
    if _RULE_CITATION.match(rest):
        return None
    separator = has_separator(rest)
    description, continuation = clean_heading_description(_strip_leaders(rest))
    if rest.lstrip().startswith(","):
        return None
    if not continuation and description and not looks_like_title(
        description,
        separator=separator,
    ):
        return None
    return identifier, description, continuation


# --------------------------------------------------------------------------
# Contents rows
# --------------------------------------------------------------------------


@dataclass
class _Band:
    top: float
    bottom: float
    cells: list[int] = field(default_factory=list)


def _bands(lines: list[TextLine], indexes: list[int]) -> list[_Band]:
    """Group a page's lines into the rows they visually share."""
    ordered = sorted(indexes, key=lambda index: (lines[index].top, lines[index].left))
    bands: list[_Band] = []
    for index in ordered:
        line = lines[index]
        placed = False
        for band in bands:
            overlap = min(band.bottom, line.bottom) - max(band.top, line.top)
            smaller = min(band.bottom - band.top, line.bottom - line.top)
            if smaller > 0 and overlap / smaller >= 0.5:
                band.cells.append(index)
                band.top = min(band.top, line.top)
                band.bottom = max(band.bottom, line.bottom)
                placed = True
                break
        if not placed:
            bands.append(_Band(line.top, line.bottom, [index]))
    for band in bands:
        band.cells.sort(key=lambda index: lines[index].left)
    return bands


def _page_number_cell(lines: list[TextLine], cells: list[int]) -> int | None:
    """The trailing cell holding this row's page number, if it has one."""
    if len(cells) < 2:
        return None
    last = cells[-1]
    if not _PAGE_NUMBER.fullmatch(lines[last].text.strip()):
        return None
    if lines[last].left < PAGE_NUMBER_COLUMN_LEFT:
        return None
    return last


def _columns_for(tables: list[dict], band: _Band) -> list[dict] | None:
    """The column bands of a detected table covering this row, if one does."""
    middle = (band.top + band.bottom) / 2
    for table in tables:
        bounds = table.get("bounds") or {}
        top = float(bounds.get("y", 0))
        bottom = top + float(bounds.get("height", 0))
        columns = table.get("columns") or []
        if len(columns) >= 2 and top - 0.005 <= middle <= bottom + 0.005:
            return columns
    return None


def _column_of(line: TextLine, columns: list[dict]) -> int:
    center = (line.left + line.right) / 2
    for index, column in enumerate(columns):
        if float(column["x0"]) - 0.002 <= center <= float(column["x1"]) + 0.002:
            return index
    return len(columns) - 1 if center > float(columns[-1]["x1"]) else 0


def _contents_rows(
    lines: list[TextLine],
    tables_by_page: dict[int, list[dict]],
) -> tuple[list[ItemTocRow], set[int]]:
    """Assemble the contents rows naming items, and the lines they consumed."""
    by_page: dict[int, list[int]] = {}
    for index, line in enumerate(lines):
        if line.text.strip():
            by_page.setdefault(line.page_index, []).append(index)

    rows: list[ItemTocRow] = []
    consumed: set[int] = set()
    carried_part = ""
    for page_index in sorted(by_page):
        bands = _bands(lines, by_page[page_index])
        anchors = [
            band for band in bands
            if _parse_header(_strip_leaders(lines[band.cells[0]].text)) is not None
        ]
        if len(anchors) < MIN_CONTENTS_ROWS:
            continue
        numbered = sum(
            1 for band in anchors if _page_number_cell(lines, band.cells) is not None
        )
        titled = any(_CONTENTS_TITLE.match(lines[index].text) for index in by_page[page_index])
        if numbered < max(MIN_CONTENTS_ROWS, len(anchors) * MIN_PAGE_NUMBER_SHARE):
            # Without a page-number column this is a run of body headings, not a
            # contents page, however many items it names.
            continue
        tables = tables_by_page.get(page_index, [])
        page_rows, page_consumed, carried_part = _contents_page_rows(
            lines, bands, tables, carried_part, titled
        )
        rows.extend(page_rows)
        consumed.update(page_consumed)
    return rows, consumed


def _contents_page_rows(
    lines: list[TextLine],
    bands: list[_Band],
    tables: list[dict],
    carried_part: str,
    titled: bool,
) -> tuple[list[ItemTocRow], set[int], str]:
    rows: list[ItemTocRow] = []
    consumed: set[int] = set()
    part = carried_part
    pending: ItemTocRow | None = None
    pending_cells: list[int] = []
    pending_description_left = 0.0

    def close() -> None:
        nonlocal pending, pending_cells, pending_description_left
        if pending is not None:
            rows.append(pending)
        pending = None
        pending_cells = []
        pending_description_left = 0.0

    for band in bands:
        first = lines[band.cells[0]]
        parsed_part = _parse_part(first.text) if len(band.cells) <= 2 else None
        if parsed_part is not None:
            close()
            part = parsed_part
            continue
        parsed = _parse_header(_strip_leaders(first.text))
        if parsed is not None:
            close()
            pending, pending_cells, pending_description_left = _row_from_band(
                lines, band, tables, part, parsed
            )
            consumed.update(band.cells)
            continue
        if pending is None:
            continue
        extended = _extend_row(
            lines, band, pending, pending_cells, pending_description_left
        )
        if extended is None:
            close()
            continue
        pending = extended
        pending_cells = list(band.cells)
        consumed.update(band.cells)
    close()
    if not titled and len(rows) < MIN_CONTENTS_ROWS:
        return [], set(), carried_part
    return rows, consumed, part


def _row_from_band(
    lines: list[TextLine],
    band: _Band,
    tables: list[dict],
    part: str,
    parsed: tuple[str, str, bool],
) -> tuple[ItemTocRow, list[int], float]:
    identifier, description, _continuation = parsed
    columns = _columns_for(tables, band)
    page_cell = _page_number_cell(lines, band.cells)

    description_cells: list[int] = []
    for index in band.cells[1:]:
        if index == page_cell:
            continue
        if columns is not None and _column_of(lines[index], columns) >= len(columns) - 1:
            # A cell in the page column that is not a page number is a leader
            # run or a stray mark, never part of the description.
            continue
        description_cells.append(index)
    parts = [description] if description else []
    parts.extend(_strip_leaders(lines[index].text) for index in description_cells)
    joined = " ".join(value for value in parts if value).strip()

    anchor = lines[band.cells[0]]
    description_left = min(
        (lines[index].left for index in description_cells),
        default=anchor.left + max(0.004, anchor.median_width),
    )
    fragments = [_fragment(lines, index) for index in band.cells]
    row = ItemTocRow(
        identifier,
        part,
        joined,
        lines[page_cell].text.strip() if page_cell is not None else "",
        columns is not None,
        tuple(fragments),
    )
    return row, list(band.cells), description_left


def _extend_row(
    lines: list[TextLine],
    band: _Band,
    pending: ItemTocRow,
    pending_cells: list[int],
    description_left: float,
) -> ItemTocRow | None:
    """Fold a wrapped contents row into the row above it.

    A description too long for its column wraps, and the page number is then
    often printed beside the wrapped remainder rather than beside the
    identifier. The remainder is recognised by starting inside the description
    column rather than at the identifier's margin -- which is exactly what
    separates it from a contents row that names something other than an item,
    such as a signature page, and which would otherwise be read as more of the
    item above it.
    """
    if not pending_cells:
        return None
    anchor = lines[pending_cells[0]]
    gap = band.top - max(lines[index].bottom for index in pending_cells)
    typical = max(anchor.median_height, 0.001)
    if gap > max(0.006, typical * 1.2):
        return None
    page_cell = _page_number_cell(lines, band.cells)
    text_cells = [index for index in band.cells if index != page_cell]
    if not text_cells:
        return None
    leftmost = lines[min(text_cells, key=lambda index: lines[index].left)]
    indent = max(0.004, anchor.median_width)
    if leftmost.left <= anchor.left + indent:
        return None
    if leftmost.left < description_left - indent:
        return None
    if leftmost.left >= PAGE_NUMBER_COLUMN_LEFT:
        return None
    continued = " ".join(
        value for value in (
            pending.description,
            *(_strip_leaders(lines[index].text) for index in text_cells),
        ) if value
    ).strip()
    if not looks_like_title(continued):
        return None
    printed_page = pending.printed_page
    if not printed_page and page_cell is not None:
        printed_page = lines[page_cell].text.strip()
    return ItemTocRow(
        pending.identifier,
        pending.part,
        continued,
        printed_page,
        pending.corroborated,
        pending.fragments + tuple(_fragment(lines, index) for index in band.cells),
    )


def _fragment(lines: list[TextLine], index: int) -> Fragment:
    start, end = trimmed_range(lines[index].text)
    return Fragment(index, start, end)


# --------------------------------------------------------------------------
# Body headings
# --------------------------------------------------------------------------


def _headers(lines: list[TextLine], skip: set[int]) -> list[ItemHeader]:
    headers: list[ItemHeader] = []
    consumed: set[int] = set()

    def is_header(index: int) -> bool:
        return index not in skip and _parse_header(lines[index].text) is not None

    part = ""
    for line_index, line in enumerate(lines):
        if line_index in skip or line_index in consumed:
            continue
        parsed_part = _parse_part(line.text)
        if parsed_part is not None:
            part = parsed_part
            continue
        parsed = _parse_header(line.text)
        if parsed is None:
            continue
        identifier, description, continuation = parsed
        if continuation:
            headers.append(
                ItemHeader(
                    identifier, part, description, True, (_fragment(lines, line_index),)
                )
            )
            continue
        extension = extend_heading(
            lines,
            line_index,
            description,
            fallback_prefix=f"Item {identifier}",
            is_header=is_header,
        )
        consumed.update(extension.consumed)
        headers.append(
            ItemHeader(
                identifier,
                part,
                extension.description,
                extension.continuation,
                extension.fragments,
            )
        )
    return headers


# --------------------------------------------------------------------------
# Catalogue and references
# --------------------------------------------------------------------------


def _catalog(rows: list[ItemTocRow], headers: list[ItemHeader]) -> list[CatalogItem]:
    parts_seen: dict[str, set[str]] = {}
    for occurrence in (*rows, *headers):
        parts_seen.setdefault(occurrence.identifier, set())
        if occurrence.part:
            parts_seen[occurrence.identifier].add(occurrence.part)

    # An identifier is keyed by its part only where the document reuses it under
    # more than one part, which is what a 10-Q does and a 10-K does not.
    split = {identifier for identifier, parts in parts_seen.items() if len(parts) > 1}

    order: list[tuple[str, str]] = []
    entries: dict[tuple[str, str], CatalogItem] = {}

    def key_for(identifier: str, part: str) -> tuple[str, str]:
        if identifier not in split:
            return (identifier, "")
        if part:
            return (identifier, part)
        # An occurrence whose part is unknown joins the first part that claimed
        # this identifier rather than inventing an entry of its own.
        for existing in order:
            if existing[0] == identifier:
                return existing
        return (identifier, "")

    for occurrence in (*rows, *headers):
        key = key_for(occurrence.identifier, occurrence.part)
        if key not in entries:
            entries[key] = CatalogItem(occurrence.identifier)
            order.append(key)
        entry = entries[key]
        if occurrence.part and not entry.part:
            entry.part = occurrence.part
        if isinstance(occurrence, ItemTocRow):
            entry.toc_entries.append(occurrence)
        else:
            entry.headers.append(occurrence)

    catalog: list[CatalogItem] = []
    for key in order:
        entry = entries[key]
        # The contents is the document's own index; a body heading is a second
        # printing of the same title and loses to it when the two disagree.
        description = canonical_description(
            [(row.description, False) for row in entry.toc_entries]
        )
        source = "toc" if description else "none"
        if not description:
            description = canonical_description(
                [(header.description, header.continuation) for header in entry.headers]
            )
            source = "heading" if description else "none"
        entry.description = description
        entry.description_source = source
        catalog.append(entry)
    return catalog


def _resolve(
    catalog: list[CatalogItem],
    candidates: list[int],
    part: str,
) -> int | None:
    """Which catalogue entry a citation means, or None when it is ambiguous.

    Only a part-keyed identifier has more than one candidate. A citation that
    names its part picks that entry; one that does not is left unpublished
    rather than attached to a guess.
    """
    if len(candidates) == 1:
        return candidates[0]
    matching = [index for index in candidates if catalog[index].part == part]
    return matching[0] if len(matching) == 1 else None


def _references(
    lines: list[TextLine],
    occupied: set[int],
    catalog: list[CatalogItem],
    by_identifier: dict[str, list[int]],
) -> list[ItemReference]:
    references: list[ItemReference] = []
    for line_index, line in enumerate(lines):
        if line_index in occupied:
            continue
        cursor = 0
        while match := _REFERENCE.search(line.text, cursor):
            identifier = _normalize_identifier(match.group("identifier"))
            end = match.end()
            if identifier is None or identifier not in by_identifier:
                cursor = end
                continue
            if _RULE_CITATION.match(line.text, end):
                cursor = end
                continue
            before = _PART_BEFORE.search(line.text, 0, match.start())
            part = (normalize_roman_identifier(before.group("part")) or "") if before else ""
            catalog_index = _resolve(catalog, by_identifier[identifier], part)
            if catalog_index is None:
                cursor = end
                continue
            source_description, reference_end = description_after_reference(line.text, end)
            references.append(
                ItemReference(
                    identifier,
                    part,
                    source_description,
                    Fragment(line_index, match.start(), reference_end),
                    catalog_index,
                )
            )
            cursor = max(reference_end, end)
    return references


def detect_items(
    lines: list[TextLine],
    tables_by_page: dict[int, list[dict]] | None = None,
) -> ItemDetection:
    """Build the canonical item catalogue and its resolved narrative citations."""
    rows, contents_lines = _contents_rows(lines, tables_by_page or {})
    headers = _headers(lines, contents_lines)
    catalog = _catalog(rows, headers)

    by_identifier: dict[str, list[int]] = {}
    for index, entry in enumerate(catalog):
        by_identifier.setdefault(entry.identifier, []).append(index)

    occupied = set(contents_lines)
    for header in headers:
        occupied.update(fragment.line_index for fragment in header.fragments)
    references = _references(lines, occupied, catalog, by_identifier)
    return ItemDetection(tuple(catalog), tuple(references))
