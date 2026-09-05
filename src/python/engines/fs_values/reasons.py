"""The vocabulary for refusing a span, mirroring the fs-values-v1 contract.

One module so the recognizer, the profile and the evidence stage cannot drift
apart, and so there is a single place to check against the contract's enum when
a rule is added.
"""
from __future__ import annotations


# Refused by the recognizer: the token could never have been a value.
IDENTIFIER = "identifier"
ALPHANUMERIC = "alphanumeric"
PARTIAL_TOKEN = "partial-token"

# Refused by the evidence stage: a well-formed value that a rule objected to.
PAGE_FURNITURE = "page-furniture"
NOTE_HEADER = "note-header"
NOTE_REFERENCE = "note-reference"
ITEM_HEADER = "item-header"
ITEM_REFERENCE = "item-reference"
ITEM_TOC_ENTRY = "item-toc-entry"
PHONE_CONTEXT = "phone-context"
IDENTIFIER_CONTEXT = "identifier-context"
SUPERSCRIPT = "superscript"
CITATION_YEAR = "citation-year"

REASONS = (
    IDENTIFIER,
    ALPHANUMERIC,
    PARTIAL_TOKEN,
    PAGE_FURNITURE,
    NOTE_HEADER,
    NOTE_REFERENCE,
    ITEM_HEADER,
    ITEM_REFERENCE,
    ITEM_TOC_ENTRY,
    PHONE_CONTEXT,
    IDENTIFIER_CONTEXT,
    SUPERSCRIPT,
    CITATION_YEAR,
)
