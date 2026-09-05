"""Find financial-statement note headings and narrative references.

Headings establish a document-level catalogue. Continuation headings attach to
that catalogue, and narrative citations resolve through it, so a bare ``Note 7``
still carries the description learned from ``Note 7 — Income Taxes``.

This module classifies text only. The detector owns geometry conversion and the
fs-values-v1 envelope.
"""
from __future__ import annotations

import re
from collections import Counter
from dataclasses import dataclass, field


_IDENTIFIER = r"(?:\d+(?:\.\d+)*|[IVXLCDM]+)"
_HEADER = re.compile(
    rf"^\s*note\s+(?P<identifier>{_IDENTIFIER})(?P<rest>.*?)\s*$",
    re.I,
)
_BARE_HEADER = re.compile(
    rf"^\s*(?P<identifier>{_IDENTIFIER})(?!\d|\.\d)(?P<rest>(?:(?:\s*[.\-\u2013\u2014:]\s*|\s+).*)?)\s*$",
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
_CONTINUED = re.compile(
    r"\s*[\[(]?\s*(?:continued|cont(?:inue)?d?\.?|cont'd)\s*[\])]?\s*$",
    re.I,
)
_ROMAN = re.compile(
    r"M{0,4}(?:CM|CD|D?C{0,3})(?:XC|XL|L?X{0,3})(?:IX|IV|V?I{0,3})"
)
_WORDS = re.compile(r"[^\W\d_]+", re.UNICODE)
_HEADING_SEPARATOR = re.compile(r"^\s*(?:[.:\-\u2013\u2014]\s*)")
_DESCRIPTION_SEPARATOR = re.compile(r"\s*(?:,|:|[\-\u2013\u2014])\s*")
_TITLE_FUNCTION_WORDS = frozenset(
    "a an and as at by for from in of on or the to with without".split()
)
_QUOTE_PAIRS = {
    '"': '"',
    "'": "'",
    "\u201c": "\u201d",
    "\u2018": "\u2019",
    "\ufffd": "\ufffd",
}


@dataclass(frozen=True)
class NoteLine:
    """One source line in document reading order."""

    page_index: int
    text: str
    left: float
    right: float
    top: float
    bottom: float
    median_width: float
    median_height: float


@dataclass(frozen=True)
class NoteFragment:
    """A half-open text range within one NoteLine."""

    line_index: int
    start: int
    end: int


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
    if re.fullmatch(r"\d+(?:\.\d+)*", raw):
        parts = [int(part) for part in raw.split(".")]
        # Keeps dates and page-number prose from masquerading as note headings
        # in a notes section while leaving ample room for large note catalogues.
        if parts[0] > 999:
            return None
        return ".".join(str(part) for part in parts)
    roman = raw.upper()
    if roman and _ROMAN.fullmatch(roman):
        return roman
    return None


def _trimmed_range(text: str) -> tuple[int, int]:
    start = len(text) - len(text.lstrip())
    end = len(text.rstrip())
    return start, end


def _without_continuation(text: str) -> tuple[str, bool]:
    match = _CONTINUED.search(text)
    if match is None:
        return text.strip(), False
    return text[: match.start()].rstrip(), True


def _clean_heading_description(rest: str) -> tuple[str, bool]:
    explicit_separator = _HEADING_SEPARATOR.match(rest) is not None
    description = _HEADING_SEPARATOR.sub("", rest, count=1).strip()
    description, continuation = _without_continuation(description)
    return description.strip(" \t\u201c\u201d\u2018\u2019\"'"), continuation


def _looks_like_title(text: str, *, separator: bool = False) -> bool:
    stripped = text.strip()
    if not stripped:
        return True
    if len(stripped) > 180 or stripped.endswith(("?", "!", ";")):
        return False
    words = _WORDS.findall(stripped)
    if not words or len(words) > 24:
        return False
    if separator:
        # A printed dash, colon or period after the identifier is strong heading
        # evidence and permits sentence-case accounting-policy titles.
        return not stripped.endswith(".")
    meaningful = [word for word in words if word.casefold() not in _TITLE_FUNCTION_WORDS]
    if not meaningful:
        return False
    titled = sum(1 for word in meaningful if word[0].isupper())
    return titled / len(meaningful) >= 0.7 and not stripped.endswith(".")


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
    separator = _HEADING_SEPARATOR.match(rest) is not None
    description, continuation = _clean_heading_description(rest)
    if rest.lstrip().startswith(",") and not continuation:
        return None
    if not continuation and description and not _looks_like_title(
        description,
        separator=separator and not bare,
    ):
        return None
    return identifier, description, continuation, not bare


def _can_extend_heading(
    current: NoteLine,
    following: NoteLine,
    title_left: float,
    *,
    following_is_header: bool,
) -> bool:
    if current.page_index != following.page_index or following_is_header:
        return False
    gap = following.top - current.bottom
    typical_height = max(current.median_height, following.median_height, 0.001)
    if gap > max(0.004, typical_height * 0.9):
        return False
    overlap = max(0.0, min(current.bottom, following.bottom) - max(current.top, following.top))
    smaller_height = min(current.bottom - current.top, following.bottom - following.top)
    same_baseline = smaller_height > 0 and overlap / smaller_height >= 0.7
    if same_baseline:
        horizontal_gap = following.left - current.right
        return (
            following.left > current.left
            and horizontal_gap >= -max(0.005, current.median_width)
            and horizontal_gap <= max(0.04, current.median_width * 5)
            and _looks_like_title(following.text)
        )
    # Wrapped title lines align under the title, not under the leading "Note".
    # This is what keeps an immediately following body subhead out of the
    # canonical note description.
    alignment_tolerance = max(0.01, current.median_width * 2)
    if abs(following.left - title_left) > alignment_tolerance:
        return False
    return _looks_like_title(following.text)


def _headers(lines: list[NoteLine]) -> list[NoteHeader]:
    headers: list[NoteHeader] = []
    consumed: set[int] = set()
    notes_section_start = next(
        (index for index, line in enumerate(lines) if _NOTES_SECTION.search(line.text)),
        None,
    )
    for line_index, line in enumerate(lines):
        if line_index in consumed:
            continue
        allow_bare = notes_section_start is not None and line_index > notes_section_start
        parsed = _parse_header(line.text, allow_bare=allow_bare)
        if parsed is None:
            continue
        identifier, description, continuation, explicit_note = parsed
        start, end = _trimmed_range(line.text)
        fragments = [NoteFragment(line_index, start, end)]
        description_offset = line.text.find(description) if description else -1
        title_left = (
            line.left + description_offset * line.median_width
            if description_offset >= 0
            else line.left + max(4, len(f"Note {identifier}")) * line.median_width
        )

        # A long title is occasionally wrapped independently of the note body.
        # Extend only across close, title-shaped lines and cap the look-ahead so
        # a run of body subheads cannot be swallowed wholesale.
        current = line
        continuation_parts: list[str] = []
        if not continuation:
            for following_index in range(line_index + 1, min(len(lines), line_index + 3)):
                following = lines[following_index]
                following_is_header = _parse_header(
                    following.text,
                    allow_bare=notes_section_start is not None and following_index > notes_section_start,
                ) is not None
                if not _can_extend_heading(
                    current,
                    following,
                    title_left,
                    following_is_header=following_is_header,
                ):
                    break
                next_start, next_end = _trimmed_range(following.text)
                continuation_parts.append(following.text[next_start:next_end])
                fragments.append(NoteFragment(following_index, next_start, next_end))
                consumed.add(following_index)
                current = following
                title_left = following.left
        if continuation_parts:
            description = " ".join([description, *continuation_parts]).strip()
            description, continued_in_extension = _without_continuation(description)
            continuation = continuation or continued_in_extension
        if not explicit_note and not description:
            continue
        headers.append(
            NoteHeader(
                identifier,
                description,
                continuation,
                explicit_note,
                tuple(fragments),
            )
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


def _canonical_description(headers: list[NoteHeader]) -> str:
    descriptions = [
        header.description
        for header in headers
        if header.description and not header.continuation
    ] or [header.description for header in headers if header.description]
    if not descriptions:
        return ""
    normalized = [" ".join(description.split()) for description in descriptions]
    counts = Counter(value.casefold() for value in normalized)
    best = max(counts, key=lambda value: (counts[value], len(value)))
    return next(value for value in normalized if value.casefold() == best)


def _description_after_reference(text: str, end: int) -> tuple[str, int]:
    separator = _DESCRIPTION_SEPARATOR.match(text, end)
    if separator is None:
        return "", end
    cursor = separator.end()
    quote = text[cursor: cursor + 1]
    if quote in _QUOTE_PAIRS:
        closing = _QUOTE_PAIRS[quote]
        close_at = text.find(closing, cursor + 1)
        if close_at != -1:
            description = text[cursor + 1:close_at].strip()
            return (description, close_at + 1) if description else ("", end)

    stop = len(text)
    for punctuation in (",", ";", ".", ")"):
        found = text.find(punctuation, cursor)
        if found != -1:
            stop = min(stop, found)
    description = text[cursor:stop].strip(" \t\u201c\u201d\u2018\u2019\"'")
    if description and _looks_like_title(description):
        return description, stop
    return "", end


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
                source_description, reference_end = _description_after_reference(line.text, end)
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
        CatalogNote(identifier, _canonical_description(by_identifier[identifier]), by_identifier[identifier])
        for identifier in order
    )
    references = tuple(_references(lines, headers, set(by_identifier)))
    return NoteDetection(notes, references)
