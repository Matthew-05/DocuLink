/**
 * Human-readable descriptions of what the value detector decided.
 *
 * These read the document-values and fs-structure contracts and nothing else.
 * The detector's rules live in Python and are not restated here: a span is
 * described by the fields it carries, never by re-deriving why it carries them.
 */
import type {
  DetectedReference,
  DetectedValue,
  NoiseReason,
  NoiseSpan,
  ReferenceKind,
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

/** What each refusal objected to, in the reader's words. */
const NOISE_EXPLANATIONS: Readonly<Record<NoiseReason, string>> = {
  "partial-token": "A token that held a figure but did not parse in full",
  "page-furniture": "On a running header or footer, repeated across pages",
  "note-header": "The number inside a financial-statement note heading",
  "item-header": "The number inside a filing item heading",
  "item-toc-entry": "Printed in a table-of-contents row naming a filing item",
  "list-marker": "The ordinal that opens a list item, so a number of it and not in it",
  "footnote-marker": "The ordinal that opens a footnote below the text it explains",
  "footnote-reference": "An indicator pointing at a footnote printed nearby",
  "superscript": "Set smaller than the page's text, so a footnote marker",
  "citation-year": "A year reached through a citation, so not a period",
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

const PRECISION_WORDS: Readonly<Record<string, string>> = {
  day: "to the day",
  month: "to the month",
  quarter: "to the quarter",
  year: "to the year",
};

export function describeNoise(entry: NoiseSpan): HoverTipContent {
  return {
    key: entry.id,
    variant: entry.reason,
    label: entry.reason,
    detail: NOISE_EXPLANATIONS[entry.reason],
    code: entry.text,
    fields: [
      { name: "recognized as", value: entry.kind },
      { name: "id", value: entry.id },
    ],
  };
}

/**
 * How the detector read one reference. A reference is a click target, so this
 * is a reader-facing tip and not only a diagnostic: it says what the span
 * identifies, and nothing is claimed about its value because it has none.
 */
export function describeReference(entry: DetectedReference): HoverTipContent {
  return {
    key: entry.id,
    variant: entry.kind,
    label: entry.kind,
    detail: `${REFERENCE_EXPLANATIONS[entry.kind]}. Captured as printed, with no value read from it.`,
    code: entry.text,
    fields: [
      ...(entry.segments && entry.segments.length > 1
        ? [{ name: "segments", value: String(entry.segments.length) }]
        : []),
      { name: "id", value: entry.id },
    ],
  };
}

/** Describe a note citation using the catalogue entry it resolved to. */
export function describeNoteReference(
  entry: DetectedReference,
  reference: FsNoteReference,
): HoverTipContent {
  return {
    key: entry.id,
    variant: entry.kind,
    label: entry.kind,
    detail: REFERENCE_EXPLANATIONS[entry.kind],
    code: entry.text,
    fields: [
      { name: "note number", value: reference.identifier },
      { name: "note description", value: reference.description || "Not provided" },
      {
        name: "description",
        value: reference.descriptionPresent ? "Printed in the citation" : "Resolved from the catalogue",
      },
    ],
  };
}

/** Describe an item citation using the catalogue entry it resolved to. */
export function describeItemReference(
  entry: DetectedReference,
  reference: FsItemReference,
): HoverTipContent {
  return {
    key: entry.id,
    variant: entry.kind,
    label: entry.kind,
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
    ],
  };
}

/** Describe the number refused inside a note heading, with the note's metadata. */
export function describeNoteHeader(
  entry: NoiseSpan,
  note: FsNote,
  header: FsHeading,
): HoverTipContent {
  return {
    key: entry.id,
    variant: entry.reason,
    label: entry.reason,
    detail: NOISE_EXPLANATIONS[entry.reason],
    code: entry.text,
    fields: [
      { name: "note number", value: note.identifier },
      { name: "note description", value: note.description || "Not provided" },
      { name: "heading", value: header.text },
      { name: "continuation", value: header.continuation ? "Yes" : "No" },
    ],
  };
}

/** Describe the number refused inside an item heading, with the item's metadata. */
export function describeItemHeader(
  entry: NoiseSpan,
  item: FsItem,
  header: FsHeading,
): HoverTipContent {
  return {
    key: entry.id,
    variant: entry.reason,
    label: entry.reason,
    detail: NOISE_EXPLANATIONS[entry.reason],
    code: entry.text,
    fields: [
      { name: "item number", value: item.identifier },
      { name: "item description", value: item.description || "Not provided" },
      ...(item.part ? [{ name: "part", value: item.part }] : []),
      { name: "description from", value: DESCRIPTION_SOURCES[item.descriptionSource] },
      { name: "heading", value: header.text },
      { name: "continuation", value: header.continuation ? "Yes" : "No" },
    ],
  };
}

/**
 * Describe a number refused inside a contents row. `corroborated` is worth
 * surfacing because it says which detector read the row: table structure, or
 * text geometry alone.
 */
export function describeItemTocEntry(
  entry: NoiseSpan,
  item: FsItem,
  tocEntry: FsItemTocEntry,
): HoverTipContent {
  return {
    key: entry.id,
    variant: entry.reason,
    label: entry.reason,
    detail: NOISE_EXPLANATIONS[entry.reason],
    code: entry.text,
    fields: [
      { name: "item number", value: item.identifier },
      { name: "item description", value: item.description || "Not provided" },
      ...(item.part ? [{ name: "part", value: item.part }] : []),
      { name: "points at page", value: tocEntry.printedPage ?? "Not printed" },
      { name: "read from", value: tocEntry.corroborated ? "A detected table" : "Text geometry" },
    ],
  };
}

function describeNumber(value: DetectedValue): string {
  const parts: string[] = [];
  if (value.normalizedValue) {
    parts.push(`Read as ${value.normalizedValue}`);
    if (value.magnitude) {
      const word = MAGNITUDE_WORDS[value.magnitude];
      if (word) parts.push(`scaled by ${word}`);
    }
    if (value.currency) parts.push(`in ${value.currency}`);
  } else {
    parts.push("No value could be derived from this text");
  }
  return `${parts.join(", ")}.`;
}

function describeDate(value: DetectedValue): string {
  if (value.dateOrder === "ambiguous") {
    return "Day and month could be either way round, so no date was derived.";
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
 * The wording is technical because the only caller today is a debug overlay.
 *
 * Note what this cannot say. The detector records no positive evidence for
 * keeping a value -- only the rules that would have refused it -- so "nothing
 * objected" is the whole truth of why a span was published. When the support
 * model lands and records which evidence carried a value, that reason belongs
 * here, and the sentence above it should go.
 */
export function describeValue(value: DetectedValue): HoverTipContent {
  const sentences: string[] = [];
  if (value.kind === "date") sentences.push(describeDate(value));
  else if (value.kind === "percent") {
    sentences.push(
      value.normalizedValue
        ? `Read as ${value.normalizedValue} percent.`
        : "No percentage could be derived from this text.",
    );
  } else sentences.push(describeNumber(value));
  sentences.push("Kept because nothing refused it and nothing claimed it.");

  const fields: Array<{ name: string; value: string }> = [];
  if (value.normalizedValue) fields.push({ name: "normalized", value: value.normalizedValue });
  if (value.currency) fields.push({ name: "currency", value: value.currency });
  if (value.magnitude) {
    fields.push({
      name: "magnitude",
      value: `x${value.magnitude} (${MAGNITUDE_WORDS[value.magnitude] ?? "unknown"})`,
    });
  }
  if (value.datePrecision) fields.push({ name: "precision", value: value.datePrecision });
  if (value.dateOrder) fields.push({ name: "order", value: value.dateOrder });
  if (value.segments && value.segments.length > 1) {
    fields.push({ name: "segments", value: String(value.segments.length) });
  }
  fields.push({ name: "confidence", value: value.confidence.toFixed(2) });
  fields.push({ name: "id", value: value.id });

  return {
    key: value.id,
    variant: value.kind,
    label: value.kind,
    detail: sentences.join(" "),
    code: value.text,
    fields,
  };
}
