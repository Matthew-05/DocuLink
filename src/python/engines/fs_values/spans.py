"""Recognize financial value spans without depending on PDF or OCR libraries."""
from __future__ import annotations

import calendar
import re
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation


@dataclass(frozen=True)
class RecognizedSpan:
    start: int
    end: int
    kind: str
    text: str
    confidence: float
    normalized_value: str = ""
    currency: str = ""
    date_precision: str = ""
    date_order: str = ""
    magnitude: int = 0


@dataclass(frozen=True)
class TextFragment:
    start: int
    end: int
    text: str


MONTHS = {
    name.lower(): index
    for index in range(1, 13)
    for name in (calendar.month_name[index], calendar.month_abbr[index])
}
MONTH_PATTERN = (
    r"Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|"
    r"Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|"
    r"Nov(?:ember)?|Dec(?:ember)?"
)
CURRENCY_CODES = {code: code for code in ("USD", "EUR", "GBP", "JPY", "CAD", "AUD", "CHF", "CNY", "INR", "KRW")}
CURRENCY_SYMBOLS = {"$": "USD", "€": "EUR", "£": "GBP", "¥": "JPY", "₹": "INR", "₩": "KRW"}

MAGNITUDES = {
    "k": 1_000,
    "th": 1_000,
    "ths": 1_000,
    "thou": 1_000,
    "thousand": 1_000,
    "thousands": 1_000,
    "m": 1_000_000,
    "mm": 1_000_000,
    "mn": 1_000_000,
    "mil": 1_000_000,
    "mln": 1_000_000,
    "million": 1_000_000,
    "millions": 1_000_000,
    "b": 1_000_000_000,
    "bn": 1_000_000_000,
    "bln": 1_000_000_000,
    "bil": 1_000_000_000,
    "billion": 1_000_000_000,
    "billions": 1_000_000_000,
    "t": 1_000_000_000_000,
    "tn": 1_000_000_000_000,
    "trn": 1_000_000_000_000,
    "tril": 1_000_000_000_000,
    "trillion": 1_000_000_000_000,
    "trillions": 1_000_000_000_000,
}
MAGNITUDE_PATTERN = (
    r"thousands?|millions?|billions?|trillions?|"
    r"thou|tril|bln|mln|trn|ths|bil|mil|bn|mn|mm|tn|th|k|m|b|t"
)

_DATE_PATTERNS = (
    (re.compile(rf"\b(?P<month>{MONTH_PATTERN})\.?\s+(?P<day>\d{{1,2}})(?:st|nd|rd|th)?\s*,?\s+(?P<year>(?:19|20)\d{{2}})\b", re.I), "mdy"),
    (re.compile(rf"\b(?P<day>\d{{1,2}})(?:st|nd|rd|th)?\s+(?P<month>{MONTH_PATTERN})\.?\s*,?\s+(?P<year>(?:19|20)\d{{2}})\b", re.I), "dmy"),
    (re.compile(r"\b(?P<year>(?:19|20)\d{2})[-/.](?P<month>0?[1-9]|1[0-2])[-/.](?P<day>0?[1-9]|[12]\d|3[01])\b"), "ymd"),
    (re.compile(r"(?<![\d.])(?P<a>0?[1-9]|[12]\d|3[01])[/.-](?P<b>0?[1-9]|[12]\d|3[01])[/.-](?P<year>(?:19|20)?\d{2})(?![\d.])"), "numeric"),
    (re.compile(rf"\b(?P<month>{MONTH_PATTERN})\.?\s+(?P<year>(?:19|20)\d{{2}})\b", re.I), "month"),
    (re.compile(r"\b(?:FY\s*)?(?P<year>(?:19|20)\d{2})\s*(?:Q(?P<q1>[1-4]))\b|\bQ(?P<q2>[1-4])\s*(?:FY\s*)?(?P<year2>(?:19|20)\d{2})\b", re.I), "quarter"),
    (re.compile(r"\b(?:FY\s*)?(?P<year>(?:19|20)\d{2})\b", re.I), "year"),
)

_WRAPPED_MONTH_DAY_RE = re.compile(
    rf"\b(?:{MONTH_PATTERN})\.?\s+\d{{1,2}}(?:st|nd|rd|th)?"
    rf"(?!\s*,?\s*(?:19|20)\d{{2}}\b)\s*,?",
    re.I,
)
_WRAPPED_YEAR_RE = re.compile(r"\b(?:19|20)\d{2}\b")

_NUMBER_RE = re.compile(
    r"(?<![\w\d])"
    r"(?P<open>\()?\s*"
    r"(?:(?P<code>USD|EUR|GBP|JPY|CAD|AUD|CHF|CNY|INR|KRW)\s*|(?P<symbol>[$€£¥₹₩])\s*)?"
    r"(?P<sign>[+-])?\s*"
    r"(?P<number>(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?|\.\d+)"
    # A bracketed negative percentage closes before its sign: "(4)%". The
    # lookahead keeps this alternative from claiming the ordinary "(1,234)"
    # closer, which the group at the end of the pattern still owns.
    r"\s*(?P<close_percent>\)(?=\s*(?:%|percent\b)))?"
    r"\s*(?P<percent>%|percent\b)?\s*"
    rf"(?P<magnitude>{MAGNITUDE_PATTERN})?\s*"
    r"(?P<close>\))?"
    r"(?![\w\d])",
    re.I,
)

_MAGNITUDE_PREFIX_RE = re.compile(rf"^\s*(?P<magnitude>{MAGNITUDE_PATTERN})\b", re.I)

# A hyphen, slash or colon binding two alphanumeric runs together makes an
# identifier, not an arithmetic expression: "10-K", "001-36743", "3:13 PM",
# "ASU 2024-03". The number pattern's own boundaries do not see these because
# none of the three is a word character.
_JOIN_CHARACTERS = "-/:"


def is_joined_fragment(text: str, start: int, end: int) -> bool:
    """Whether `text[start:end]` is one piece of a larger joined-up token.

    The join has to be immediately adjacent on both sides of the punctuation:
    "par value: 50,400,000" keeps its number because of the space after the colon.
    A matched span can open or close on whitespace, so adjacency is measured from
    the span's first and last printing characters rather than from its offsets.
    """
    while start < end and text[start].isspace():
        start += 1
    while end > start and text[end - 1].isspace():
        end -= 1
    if start >= end:
        return False
    if start >= 2 and text[start - 1] in _JOIN_CHARACTERS and text[start - 2].isalnum():
        return True
    if end + 1 < len(text) and text[end] in _JOIN_CHARACTERS and text[end + 1].isalnum():
        return True
    return False


def _month_number(value: str) -> int:
    return MONTHS[value.rstrip(".").lower()]


def _valid_date(year: int, month: int, day: int) -> bool:
    try:
        calendar.monthrange(year, month)[1]
        return 1 <= day <= calendar.monthrange(year, month)[1]
    except (ValueError, IndexError):
        return False


def _date_span(match: re.Match[str], mode: str) -> RecognizedSpan | None:
    groups = match.groupdict()
    if mode == "quarter":
        year = int(groups.get("year") or groups.get("year2") or 0)
        quarter = int(groups.get("q1") or groups.get("q2") or 0)
        normalized = f"{year:04d}-Q{quarter}"
        precision, order = "quarter", "ymd"
    elif mode == "year":
        year = int(groups["year"])
        normalized, precision, order = f"{year:04d}", "year", "ymd"
    elif mode == "month":
        year, month = int(groups["year"]), _month_number(groups["month"])
        normalized, precision, order = f"{year:04d}-{month:02d}", "month", "mdy"
    elif mode == "numeric":
        first, second = int(groups["a"]), int(groups["b"])
        year_text = groups["year"]
        year = int(year_text) + (2000 if len(year_text) == 2 and int(year_text) < 70 else 1900 if len(year_text) == 2 else 0)
        if first <= 12 and second <= 12:
            normalized, order = "", "ambiguous"
        elif first <= 12:
            month, day, order = first, second, "mdy"
            if not _valid_date(year, month, day):
                return None
            normalized = f"{year:04d}-{month:02d}-{day:02d}"
        else:
            day, month, order = first, second, "dmy"
            if not _valid_date(year, month, day):
                return None
            normalized = f"{year:04d}-{month:02d}-{day:02d}"
        precision = "day"
    else:
        year = int(groups["year"])
        month = int(groups["month"]) if groups["month"].isdigit() else _month_number(groups["month"])
        day = int(groups["day"])
        if not _valid_date(year, month, day):
            return None
        normalized, precision, order = f"{year:04d}-{month:02d}-{day:02d}", "day", mode
    return RecognizedSpan(
        match.start(), match.end(), "date", match.group(0), 0.98 if precision == "day" else 0.92,
        normalized, date_precision=precision, date_order=order,
    )


def _overlaps(start: int, end: int, occupied: list[tuple[int, int]]) -> bool:
    return any(start < right and end > left for left, right in occupied)


def _canonical_decimal(text: str, negative: bool) -> str:
    try:
        value = Decimal(text.replace(",", ""))
    except InvalidOperation:
        return ""
    if negative:
        value = -abs(value)
    rendered = format(value, "f")
    if "." in rendered:
        rendered = rendered.rstrip("0").rstrip(".")
    return rendered or "0"


def _scaled_decimal(text: str, negative: bool, magnitude: int) -> str:
    canonical = _canonical_decimal(text, negative)
    if not canonical or not magnitude:
        return canonical
    return _canonical_decimal(str(Decimal(canonical) * magnitude), negative=False)


def recognize_magnitude_prefix(text: str) -> tuple[int, str, int] | None:
    """Return the first magnitude token when it begins the supplied line."""
    match = _MAGNITUDE_PREFIX_RE.match(text)
    if match is None:
        return None
    modifier = match.group("magnitude")
    return match.end(), modifier, MAGNITUDES[modifier.lower()]


def wrapped_date_heads(text: str) -> list[TextFragment]:
    """Month/day fragments that still need a year from the line below."""
    return [
        TextFragment(match.start(), match.end(), match.group(0).strip())
        for match in _WRAPPED_MONTH_DAY_RE.finditer(text)
    ]


def wrapped_date_years(text: str) -> list[TextFragment]:
    """Four-digit year fragments that can finish a wrapped month/day."""
    return [TextFragment(match.start(), match.end(), match.group(0)) for match in _WRAPPED_YEAR_RE.finditer(text)]


def recognize_wrapped_date(head: str, year: str) -> RecognizedSpan | None:
    """Build the ordinary date span produced when two visual fragments are joined."""
    combined = f"{head.rstrip()} {year.strip()}"
    return next(
        (span for span in recognize_spans(combined) if span.kind == "date" and span.date_precision == "day"),
        None,
    )


def recognize_spans(text: str) -> list[RecognizedSpan]:
    """Return non-overlapping date/percent/number spans in source order."""
    results: list[RecognizedSpan] = []
    occupied: list[tuple[int, int]] = []
    for pattern, mode in _DATE_PATTERNS:
        for match in pattern.finditer(text):
            if _overlaps(match.start(), match.end(), occupied):
                continue
            span = _date_span(match, mode)
            if span is not None:
                results.append(span)
                occupied.append((span.start, span.end))

    for match in _NUMBER_RE.finditer(text):
        start, end = match.span()
        if _overlaps(start, end, occupied):
            continue
        open_paren = match.group("open")
        close_paren = match.group("close") or match.group("close_percent")
        if bool(open_paren) != bool(close_paren):
            # Trim unmatched optional punctuation rather than claiming prose parens.
            if open_paren:
                start = match.start("number")
            else:
                # A closer with no opener still leaves the percentage behind it.
                end = match.end("percent") if match.group("percent") else match.end("number")
        number_text = match.group("number")
        negative = match.group("sign") == "-" or bool(open_paren and close_paren)
        magnitude_text = match.group("magnitude") or ""
        magnitude = MAGNITUDES.get(magnitude_text.lower(), 0)
        normalized = _scaled_decimal(number_text, negative, magnitude)
        if not normalized:
            continue
        code = (match.group("code") or "").upper()
        currency = CURRENCY_CODES.get(code, "") or CURRENCY_SYMBOLS.get(match.group("symbol") or "", "")
        percent = bool(match.group("percent"))
        if percent and magnitude:
            # A magnitude following "percent" belongs to surrounding prose, not the percentage.
            magnitude = 0
            normalized = _canonical_decimal(number_text, negative)
            end = match.end("percent")
        confidence = 0.99 if percent or currency else 0.94 if "," in number_text else 0.82 if "." in number_text else 0.62
        results.append(RecognizedSpan(
            start, end, "percent" if percent else "number", text[start:end].strip(), confidence,
            normalized, magnitude=magnitude, currency=currency,
        ))
        occupied.append((start, end))

    return sorted(results, key=lambda span: (span.start, span.end))
