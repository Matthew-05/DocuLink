"""The vocabulary for classifying a span, mirroring the document-values-v1 contract.

Every recognized span lands in exactly one of three categories. A **value**
measures: a quantity, a proportion, a point in time. A **reference** identifies:
an invoice number, an account number, a citation naming a note. Both are click
targets, on every document alike -- an auditor sampling invoices captures the
invoice number as readily as the amount, so category follows the span and never
the document. **Noise** is an artifact of setting the page, or a token too
damaged to read; it is diagnostic only and is never a click target.

One module so the recognizer, the profile, the classification stage and the two
detectors cannot drift apart, and so there is a single place to check against
the contract's enums when a rule is added.
"""
from __future__ import annotations


VALUE = "value"
REFERENCE = "reference"
NOISE = "noise"

CATEGORIES = (VALUE, REFERENCE, NOISE)


# --- reference kinds -------------------------------------------------------
# What the reference identifies. A citation resolves to a catalogue entry in
# fs-structure-v1; the rest stand alone.

IDENTIFIER = "identifier"
PHONE = "phone"
POSTAL = "postal"
TAX_ID = "tax-id"
SECURITY_ID = "security-id"
NOTE = "note"
ITEM = "item"

REFERENCE_KINDS = (
    IDENTIFIER,
    PHONE,
    POSTAL,
    TAX_ID,
    SECURITY_ID,
    NOTE,
    ITEM,
)


# --- noise reasons ---------------------------------------------------------
# Refused by the recognizer: the token was too damaged to read as anything.
PARTIAL_TOKEN = "partial-token"

# Refused by classification: well-formed, and still not a value.
PAGE_FURNITURE = "page-furniture"
NOTE_HEADER = "note-header"
ITEM_HEADER = "item-header"
ITEM_TOC_ENTRY = "item-toc-entry"
LIST_MARKER = "list-marker"
FOOTNOTE_MARKER = "footnote-marker"
FOOTNOTE_REFERENCE = "footnote-reference"
SUPERSCRIPT = "superscript"
CITATION_YEAR = "citation-year"

NOISE_REASONS = (
    PARTIAL_TOKEN,
    PAGE_FURNITURE,
    NOTE_HEADER,
    ITEM_HEADER,
    ITEM_TOC_ENTRY,
    LIST_MARKER,
    FOOTNOTE_MARKER,
    FOOTNOTE_REFERENCE,
    SUPERSCRIPT,
    CITATION_YEAR,
)


# --- token shapes ----------------------------------------------------------
# What `spans.token_shape` says about a token that could not be read as a value.
# The first two are printed data under a shape a value never takes, so they
# become references; the third is damage, and stays noise.

ALPHANUMERIC = "alphanumeric"

TOKEN_SHAPE_CATEGORY = {
    IDENTIFIER: (REFERENCE, IDENTIFIER),
    ALPHANUMERIC: (REFERENCE, IDENTIFIER),
    PARTIAL_TOKEN: (NOISE, PARTIAL_TOKEN),
}
