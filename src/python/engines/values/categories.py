"""The vocabulary for classifying a span, mirroring the document-values-v1 contract.

Every recognized span lands in exactly one of four categories. A **value**
measures: a quantity, a proportion, a point in time. A **reference** identifies
something about the document itself or something outside the document: an invoice number, 
an account number.**Structure**is the document indexing itself: the number in a note heading,
the ordinal that opens a footnote. **Noise** is what is left -- damage, or a span nothing could
identify -- and it should shrink as detection improves.

Category says what a span *is*. Whether it is a click target is a separate fact,
carried per span by `clickable`, because the two are deliberately separable: a
kind of span can become capturable without moving category. `CLICKABLE_BY_DEFAULT`
below is the policy in one place.

One module so the recognizer, the profile, the classification stage and the two
detectors cannot drift apart, and so there is a single place to check against
the contract's enums when a rule is added.
"""
from __future__ import annotations


VALUE = "value"
REFERENCE = "reference"
STRUCTURE = "structure"
NOISE = "noise"

CATEGORIES = (VALUE, REFERENCE, STRUCTURE, NOISE)


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


# --- structure kinds -------------------------------------------------------
# The apparatus a document indexes itself with. Printed text with meaning, which
# is what separates these from noise, and measuring nothing, which is what
# separates them from values.

NOTE_HEADER = "note-header"
ITEM_HEADER = "item-header"
ITEM_TOC_ENTRY = "item-toc-entry"
LIST_MARKER = "list-marker"
FOOTNOTE_MARKER = "footnote-marker"
FOOTNOTE_REFERENCE = "footnote-reference"

STRUCTURE_KINDS = (
    NOTE_HEADER,
    ITEM_HEADER,
    ITEM_TOC_ENTRY,
    LIST_MARKER,
    FOOTNOTE_MARKER,
    FOOTNOTE_REFERENCE,
)


# --- noise reasons ---------------------------------------------------------
# What is left once values, references and structure have been taken: damage,
# and spans the detector refused without being able to say what they were.
#
# The first four name what a span is. `unsupported` is the exception and the
# only one that refuses a figure the recognizer read in full: it names an
# absence -- a number in a sentence with nothing about it saying it measures
# anything -- so it is the reason most likely to be wrong, and the one to look
# at first when a real value goes missing.

PARTIAL_TOKEN = "partial-token"
PAGE_FURNITURE = "page-furniture"
SUPERSCRIPT = "superscript"
CITATION_YEAR = "citation-year"
UNSUPPORTED = "unsupported"

NOISE_REASONS = (
    PARTIAL_TOKEN,
    PAGE_FURNITURE,
    SUPERSCRIPT,
    CITATION_YEAR,
    UNSUPPORTED,
)


# --- clickability ----------------------------------------------------------
# Whether a span becomes a click target, by category, in one place. Values and
# references are captured by readers; structure is drawn only as a diagnostic
# today, and noise never has anything to capture.
#
# A kind that should be clickable against its category's default belongs in
# CLICKABLE_KINDS, which is deliberately empty: nothing needs the exception yet,
# and the field exists so that adding one is a line here rather than a contract
# change.

CLICKABLE_BY_DEFAULT = {
    VALUE: True,
    REFERENCE: True,
    STRUCTURE: False,
    NOISE: False,
}

CLICKABLE_KINDS: dict[str, bool] = {}


def is_clickable(category: str, kind: str) -> bool:
    """Whether the viewer should make a click target of this span."""
    if category == NOISE:
        return False
    return CLICKABLE_KINDS.get(kind, CLICKABLE_BY_DEFAULT[category])


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
