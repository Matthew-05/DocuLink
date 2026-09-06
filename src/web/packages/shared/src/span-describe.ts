/**
 * Human-readable descriptions of what the value detector decided.
 *
 * These read the document-values and fs-structure contracts and nothing else.
 * The detector's rules live in Python and are not restated here: a span is
 * described by the fields it carries, never by re-deriving why it carries them.
 *
 * Every tip names its category at the top and ends with the same four facts —
 * its specific value type, whether it is a click target, which page it is on,
 * and its id — because those are the ones you reach for when something is in
 * the wrong layer or a link went to the wrong place. What comes before them is
 * whatever that category has to say.
 */
import type {
  DetectedReference,
  DetectedStructure,
  DetectedValue,
  NoiseReason,
  NoiseSpan,
  ReferenceKind,
  StructureKind,
  ValueContext,
} from "./document-values-decoder.js";
import type {
  FsHeading,
  FsItem,
  FsItemTocEntry,
  FsNote,
  FsNoteReference,
  FsItemReference,
} from "./fs-structure-decoder.js";
import type { HoverTipContent } from "./hover-tip.js";

/** Where a span sits, and what its page says its figures are denominated in. */
export interface SpanTipContext {
  pageIndex: number;
  pageContext?: ValueContext;
  documentContext?: ValueContext;
}

type Field = { name: string; value: string };

/** What each refusal objected to, in the reader's words. */
const NOISE_EXPLANATIONS: Readonly<Record<NoiseReason, string>> = {
  "partial-token": "A token that held a figure but did not parse in full",
  "page-furniture": "On a running header or footer, repeated across pages",
  "superscript": "Set smaller than the page's text, so a mark rather than a figure",
  "citation-year": "A year reached through a citation, so not a period",
  "unsupported": "A bare number in a sentence, with nothing about it saying it measures anything",
};

/** What each reference identifies, in the reader's words. */
const REFERENCE_EXPLANATIONS: Readonly<Record<ReferenceKind, string>> = {
  "identifier": "A printed reference number — an invoice, account, form or document number",
  "phone": "A telephone number",
  "postal": "A postal or ZIP code",
  "tax-id": "A tax or employer identification number",
  "security-id": "A security identifier — an ISIN, CUSIP, SEDOL or ticker",
  "note": "A citation naming a financial-statement note",
  "item": "A citation naming a filing item",
};

/** What each structural role is, in the reader's words. */
const STRUCTURE_EXPLANATIONS: Readonly<Record<StructureKind, string>> = {
  "note-header": "The number inside a financial-statement note heading",
  "item-header": "The number inside a filing item heading",
  "item-toc-entry": "Printed in a table-of-contents row naming a filing item",
  "list-marker": "The ordinal that opens a list item, so a number of it and not in it",
  "footnote-marker": "The ordinal that opens a footnote below the text it explains",
  "footnote-reference": "An indicator pointing at a footnote printed nearby",
};

const DESCRIPTION_SOURCES: Readonly<Record<FsItem["descriptionSource"], string>> = {
  toc: "The table of contents",
  heading: "A heading in the body",
  none: "Neither — none was found",
};

const MAGNITUDE_WORDS: Readonly<Record<number, string>> = {
  1000: "thousands",
  1000000: "millions",
  1000000000: "billions",
  1000000000000: "trillions",
};

const SCALE_WORDS: Readonly<Record<number, string>> = {
  1: "ones",
  1000: "thousands",
  1000000: "millions",
  1000000000: "billions",
};

const PRECISION_WORDS: Readonly<Record<string, string>> = {
  day: "to the day",
  month: "to the month",
  quarter: "to the quarter",
  year: "to the year",
};

const CURRENCY_MARKS = ["$", "€", "£", "¥", "₹", "₩"];
const CURRENCY_CODES = ["USD", "EUR", "GBP", "JPY", "CAD", "AUD", "CHF", "CNY", "INR", "KRW"];

/** The four facts every tip ends with, whatever category it describes. */
function provenance(
  valueType: string,
  span: { id: string; clickable: boolean },
  context?: SpanTipContext,
): Field[] {
  return [
    { name: "value type", value: valueType },
    { name: "click target", value: span.clickable ? "Yes" : "No" },
    ...(context ? [{ name: "page", value: String(context.pageIndex + 1) }] : []),
    { name: "id", value: span.id },
  ];
}

/** What the page and document say their figures are denominated in. */
function contextFields(context?: SpanTipContext): Field[] {
  if (!context) return [];
  const fields: Field[] = [];
  const scale = context.pageContext?.scale;
  if (scale !== undefined) {
    // Worth stating because scale is deliberately NOT folded into
    // normalizedValue: it is what the page's caption claims, not what a
    // modifier attached to this number said.
    fields.push({
      name: "page states",
      value: `in ${SCALE_WORDS[scale] ?? String(scale)} (not applied)`,
    });
  }
  return fields;
}

export function describeNoise(entry: NoiseSpan, context?: SpanTipContext): HoverTipContent {
  return {
    key: entry.id,
    variant: "noise",
    label: "noise",
    detail: `${NOISE_EXPLANATIONS[entry.reason]}. Refused, and published only as a diagnostic.`,
    code: entry.text,
    fields: [
      { name: "refusal reason", value: entry.reason },
      ...provenance(entry.kind, entry, context),
    ],
  };
}

/**
 * How the detector read one reference. A reference is normally a click target,
 * so this is reader-facing and not only a diagnostic: it says what the span
 * identifies, and claims nothing about its value because it has none.
 */
export function describeReference(
  entry: DetectedReference,
  context?: SpanTipContext,
): HoverTipContent {
  return {
    key: entry.id,
    variant: "reference",
    label: "reference",
    detail: `${REFERENCE_EXPLANATIONS[entry.kind]}. Captured as printed, with no value read from it.`,
    code: entry.text,
    fields: [
      ...(entry.segments && entry.segments.length > 1
        ? [{ name: "segments", value: String(entry.segments.length) }]
        : []),
      ...provenance(entry.kind, entry, context),
    ],
  };
}

/** A structure span with nothing in the catalogues to say more about it. */
export function describeStructure(
  entry: DetectedStructure,
  context?: SpanTipContext,
): HoverTipContent {
  return {
    key: entry.id,
    variant: "structure",
    label: "structure",
    detail: `${STRUCTURE_EXPLANATIONS[entry.kind]}. Part of how the document indexes itself, so it measures nothing.`,
    code: entry.text,
    fields: provenance(entry.kind, entry, context),
  };
}

/** Describe a note citation using the catalogue entry it resolved to. */
export function describeNoteReference(
  entry: DetectedReference,
  reference: FsNoteReference,
  context?: SpanTipContext,
): HoverTipContent {
  return {
    key: entry.id,
    variant: "reference",
    label: "reference",
    detail: REFERENCE_EXPLANATIONS[entry.kind],
    code: entry.text,
    fields: [
      { name: "note number", value: reference.identifier },
      { name: "note description", value: reference.description || "Not provided" },
      {
        name: "description",
        value: reference.descriptionPresent ? "Printed in the citation" : "Resolved from the catalogue",
      },
      ...provenance(entry.kind, entry, context),
    ],
  };
}

/** Describe an item citation using the catalogue entry it resolved to. */
export function describeItemReference(
  entry: DetectedReference,
  reference: FsItemReference,
  context?: SpanTipContext,
): HoverTipContent {
  return {
    key: entry.id,
    variant: "reference",
    label: "reference",
    detail: REFERENCE_EXPLANATIONS[entry.kind],
    code: entry.text,
    fields: [
      { name: "item number", value: reference.identifier },
      { name: "item description", value: reference.description || "Not provided" },
      ...(reference.part ? [{ name: "part", value: reference.part }] : []),
      {
        name: "description",
        value: reference.descriptionPresent ? "Printed in the citation" : "Resolved from the catalogue",
      },
      ...provenance(entry.kind, entry, context),
    ],
  };
}

/** Describe the number inside a note heading, with the note's metadata. */
export function describeNoteHeader(
  entry: DetectedStructure,
  note: FsNote,
  header: FsHeading,
  context?: SpanTipContext,
): HoverTipContent {
  return {
    key: entry.id,
    variant: "structure",
    label: "structure",
    detail: STRUCTURE_EXPLANATIONS[entry.kind],
    code: entry.text,
    fields: [
      { name: "note number", value: note.identifier },
      { name: "note description", value: note.description || "Not provided" },
      { name: "heading", value: header.text },
      { name: "continuation", value: header.continuation ? "Yes" : "No" },
      { name: "occurrences", value: String(note.headers.length) },
      ...provenance(entry.kind, entry, context),
    ],
  };
}

/** Describe the number inside an item heading, with the item's metadata. */
export function describeItemHeader(
  entry: DetectedStructure,
  item: FsItem,
  header: FsHeading,
  context?: SpanTipContext,
): HoverTipContent {
  return {
    key: entry.id,
    variant: "structure",
    label: "structure",
    detail: STRUCTURE_EXPLANATIONS[entry.kind],
    code: entry.text,
    fields: [
      { name: "item number", value: item.identifier },
      { name: "item description", value: item.description || "Not provided" },
      ...(item.part ? [{ name: "part", value: item.part }] : []),
      { name: "description from", value: DESCRIPTION_SOURCES[item.descriptionSource] },
      { name: "heading", value: header.text },
      { name: "continuation", value: header.continuation ? "Yes" : "No" },
      ...provenance(entry.kind, entry, context),
    ],
  };
}

/**
 * Describe a number inside a contents row. `corroborated` is worth surfacing
 * because it says which detector read the row: table structure, or text
 * geometry alone.
 */
export function describeItemTocEntry(
  entry: DetectedStructure,
  item: FsItem,
  tocEntry: FsItemTocEntry,
  context?: SpanTipContext,
): HoverTipContent {
  return {
    key: entry.id,
    variant: "structure",
    label: "structure",
    detail: STRUCTURE_EXPLANATIONS[entry.kind],
    code: entry.text,
    fields: [
      { name: "item number", value: item.identifier },
      { name: "item description", value: item.description || "Not provided" },
      ...(item.part ? [{ name: "part", value: item.part }] : []),
      { name: "points at page", value: tocEntry.printedPage ?? "Not printed" },
      { name: "read from", value: tocEntry.corroborated ? "A detected table" : "Text geometry" },
      ...provenance(entry.kind, entry, context),
    ],
  };
}

/** Whether the span's own text carries the currency it was published with. */
function currencyIsPrinted(value: DetectedValue): boolean {
  if (!value.currency) return false;
  const text = value.text.toUpperCase();
  return CURRENCY_MARKS.some((mark) => value.text.includes(mark))
    || CURRENCY_CODES.some((code) => text.includes(code));
}

function describeNumber(value: DetectedValue): string {
  const parts: string[] = [];
  if (value.normalizedValue) {
    parts.push(`Read as ${value.normalizedValue}`);
    if (value.magnitude) {
      const word = MAGNITUDE_WORDS[value.magnitude];
      if (word) parts.push(`with the ${word} modifier already applied`);
    }
    if (value.currency) parts.push(`in ${value.currency}`);
  } else {
    parts.push("No value could be derived from this text");
  }
  return `${parts.join(", ")}.`;
}

function describeDate(value: DetectedValue): string {
  if (value.dateOrder === "ambiguous") {
    return "Day and month could be either way round, so no date was derived — "
      + "nothing downstream may assume a reading the detector refused to make.";
  }
  if (!value.normalizedValue) return "No date could be derived from this text.";
  const precision = value.datePrecision ? PRECISION_WORDS[value.datePrecision] : undefined;
  return precision
    ? `Read as ${value.normalizedValue}, ${precision}.`
    : `Read as ${value.normalizedValue}.`;
}

/**
 * How the detector read one published value.
 *
 * Note what this cannot say. The detector records no positive evidence for
 * keeping a value -- only the rules that would have refused it -- so "nothing
 * objected" is the whole truth of why a span was published. When the support
 * model lands and records which evidence carried a value, that reason belongs
 * here, and the sentence above it should go.
 */
export function describeValue(value: DetectedValue, context?: SpanTipContext): HoverTipContent {
  const sentences: string[] = [];
  if (value.kind === "date") sentences.push(describeDate(value));
  else if (value.kind === "percent") {
    sentences.push(
      value.normalizedValue
        ? `Read as ${value.normalizedValue} percent — the printed figure, not a fraction of one.`
        : "No percentage could be derived from this text.",
    );
  } else sentences.push(describeNumber(value));
  sentences.push("Kept because nothing refused it and nothing claimed it.");

  const fields: Field[] = [];
  if (value.normalizedValue) fields.push({ name: "normalized", value: value.normalizedValue });
  if (value.currency) {
    fields.push({
      name: "currency",
      value: currencyIsPrinted(value)
        ? value.currency
        : `${value.currency} (inherited, not printed here)`,
    });
  }
  if (value.magnitude) {
    fields.push({
      name: "magnitude",
      value: `x${value.magnitude} (${MAGNITUDE_WORDS[value.magnitude] ?? "unknown"}), applied`,
    });
  }
  if (value.datePrecision) fields.push({ name: "precision", value: value.datePrecision });
  if (value.dateOrder) fields.push({ name: "order", value: value.dateOrder });
  if (value.segments && value.segments.length > 1) {
    fields.push({
      name: "segments",
      value: `${value.segments.length} (${value.segments.map((segment) => segment.text).join(" + ")})`,
    });
  }
  fields.push(...contextFields(context));
  fields.push({ name: "confidence", value: `${value.confidence.toFixed(2)} (shape, not certainty)` });
  fields.push(...provenance(value.kind, value, context));

  return {
    key: value.id,
    variant: "value",
    label: "value",
    detail: sentences.join(" "),
    code: value.text,
    fields,
  };
}
