"""Find financial-statement note headings and narrative references.

Headings establish a document-level catalogue. Continuation headings attach to
that catalogue, and narrative citations resolve through it, so a bare ``Note 7``
still carries the description learned from ``Note 7 — Income Taxes``.

The heading mechanics themselves live in ``headings``; this module supplies the
note grammar and the policy deciding which candidate headings survive.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field

from .headings import (
    Fragment,
    HeadingLine,
    canonical_description,
    clean_heading_description,
    description_after_reference,
    extend_heading,
    has_separator,
    looks_like_title,
    normalize_decimal_identifier,
    normalize_roman_identifier,
    trimmed_range,
)


# Retained so callers naming the note vocabulary keep reading naturally.
NoteLine = HeadingLine
NoteFragment = Fragment

_IDENTIFIER = r"(?:\d+(?:\.\d+)*|[IVXLCDM]+)"
_HEADER = re.compile(
    rf"^\s*note\s+(?P<identifier>{_IDENTIFIER})(?P<rest>.*?)\s*$",
    re.I,
)
_BARE_HEADER = re.compile(
    rf"^\s*(?P<identifier>{_IDENTIFIER})(?!\d|\.\d)(?P<rest>(?:(?:\s*[.\-–—:]\s*|\s+).*)?)\s*$",
    re.I,
)
_NOTES_SECTION = re.compile(
    r"\bnotes?\s+to\s+(?:the\s+)?(?:consolidated\s+)?financial statements\b",
    re.I,
)
_REFERENCE = re.compile(
    rf"\bnotes?\s+(?P<identifier>{_IDENTIFIER})\b",
    re.I,
)
_NEXT_IDENTIFIER = re.compile(
    rf"\s*(?:,\s*(?:and\s+)?|\band\s+|&\s*)(?P<identifier>{_IDENTIFIER})\b",
    re.I,
)


@dataclass(frozen=True)
class NoteHeader:
    identifier: str
    description: str
    continuation: bool
    explicit_note: bool
    fragments: tuple[NoteFragment, ...]


@dataclass(frozen=True)
class NoteReference:
    identifiers: tuple[str, ...]
    source_description: str
    fragment: NoteFragment


@dataclass
class CatalogNote:
    identifier: str
    description: str = ""
    headers: list[NoteHeader] = field(default_factory=list)


@dataclass(frozen=True)
class NoteDetection:
    notes: tuple[CatalogNote, ...]
    references: tuple[NoteReference, ...]


def _normalize_identifier(raw: str) -> str | None:
    decimal = normalize_decimal_identifier(raw)
    if decimal is not None:
        return decimal
    if re.fullmatch(r"\d+(?:\.\d+)*", raw):
        return None
    return normalize_roman_identifier(raw)


def _parse_header(text: str, *, allow_bare: bool = False) -> tuple[str, str, bool, bool] | None:
    match = _HEADER.match(text)
    bare = False
    if match is None and allow_bare:
        match = _BARE_HEADER.match(text)
        bare = match is not None
    if match is None:
        return None
    identifier = _normalize_identifier(match.group("identifier"))
    if identifier is None:
        return None
    rest = match.group("rest")
    separator = has_separator(rest)
    description, continuation = clean_heading_description(rest)
    if rest.lstrip().startswith(",") and not continuation:
        return None
    if not continuation and description and not looks_like_title(
        description,
        separator=separator and not bare,
    ):
        return None
    return identifier, description, continuation, not bare


def _headers(lines: list[NoteLine]) -> list[NoteHeader]:
    headers: list[NoteHeader] = []
    consumed: set[int] = set()
    notes_section_start = next(
        (index for index, line in enumerate(lines) if _NOTES_SECTION.search(line.text)),
        None,
    )

    def bare_allowed(index: int) -> bool:
        return notes_section_start is not None and index > notes_section_start

    def is_header(index: int) -> bool:
        return _parse_header(lines[index].text, allow_bare=bare_allowed(index)) is not None

    for line_index, line in enumerate(lines):
        if line_index in consumed:
            continue
        parsed = _parse_header(line.text, allow_bare=bare_allowed(line_index))
        if parsed is None:
            continue
        identifier, description, continuation, explicit_note = parsed
        fragments: tuple[NoteFragment, ...]
        if continuation:
            start, end = trimmed_range(line.text)
            fragments = (NoteFragment(line_index, start, end),)
        else:
            extension = extend_heading(
                lines,
                line_index,
                description,
                fallback_prefix=f"Note {identifier}",
                is_header=is_header,
            )
            description = extension.description
            continuation = extension.continuation
            fragments = extension.fragments
            consumed.update(extension.consumed)
        if not explicit_note and not description:
            continue
        headers.append(
            NoteHeader(identifier, description, continuation, explicit_note, fragments)
        )
    if any(header.explicit_note for header in headers):
        # Financial reports use one heading convention consistently. Once an
        # explicit "Note N" catalogue exists, later bare numbered material is
        # schedules, exhibits or signatures rather than a second note system.
        return [header for header in headers if header.explicit_note]

    numeric_bare = any(
        not header.explicit_note and header.identifier[0].isdigit()
        for header in headers
    )
    if numeric_bare:
        # A numbered notes section can be followed by signature blocks whose
        # initials happen to be valid Roman numerals (for example "D. Name").
        # Do not mix identifier systems unless the document says "Note D"
        # explicitly.
        headers = [
            header
            for header in headers
            if header.explicit_note or header.identifier[0].isdigit()
        ]
    return headers


def _references(
    lines: list[NoteLine],
    headers: list[NoteHeader],
    known: set[str],
) -> list[NoteReference]:
    occupied_lines = {
        fragment.line_index
        for header in headers
        for fragment in header.fragments
    }
    references: list[NoteReference] = []
    for line_index, line in enumerate(lines):
        if line_index in occupied_lines:
            continue
        cursor = 0
        while match := _REFERENCE.search(line.text, cursor):
            identifier = _normalize_identifier(match.group("identifier"))
            if identifier is None:
                cursor = match.end()
                continue
            identifiers = [identifier]
            end = match.end()
            if line.text[match.start():].casefold().startswith("notes"):
                while next_match := _NEXT_IDENTIFIER.match(line.text, end):
                    next_identifier = _normalize_identifier(next_match.group("identifier"))
                    if next_identifier is None:
                        break
                    identifiers.append(next_identifier)
                    end = next_match.end()

            source_description = ""
            reference_end = end
            if len(identifiers) == 1:
                source_description, reference_end = description_after_reference(line.text, end)
            resolved = tuple(value for value in identifiers if value in known)
            if resolved:
                references.append(
                    NoteReference(
                        resolved,
                        source_description,
                        NoteFragment(line_index, match.start(), reference_end),
                    )
                )
            cursor = max(reference_end, match.end())
    return references


def detect_notes(lines: list[NoteLine]) -> NoteDetection:
    """Build the canonical note catalogue and its resolved narrative citations."""
    headers = _headers(lines)
    by_identifier: dict[str, list[NoteHeader]] = {}
    order: list[str] = []
    for header in headers:
        if header.identifier not in by_identifier:
            by_identifier[header.identifier] = []
            order.append(header.identifier)
        by_identifier[header.identifier].append(header)

    notes = tuple(
        CatalogNote(
            identifier,
            canonical_description(
                [
                    (header.description, header.continuation)
                    for header in by_identifier[identifier]
                ]
            ),
            by_identifier[identifier],
        )
        for identifier in order
    )
    references = tuple(_references(lines, headers, set(by_identifier)))
    return NoteDetection(notes, references)
