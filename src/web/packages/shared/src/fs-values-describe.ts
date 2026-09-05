/**
 * Human-readable descriptions of what the financial-value detector decided.
 *
 * These read the fs-values contract and nothing else. The detector's rules live
 * in Python and are not restated here: a value is described by the fields it
 * carries, never by re-deriving why it carries them.
 */
import type {
  FinancialValue,
  FsNoiseReason,
  FsNoiseValue,
  FsNote,
  FsNoteHeader,
} from "./fs-values-decoder.js";
import type { HoverTipContent } from "./hover-tip.js";

/** What each suppressor objected to, in the reader's words. */
const NOISE_EXPLANATIONS: Readonly<Record<FsNoiseReason, string>> = {
  "identifier": "Part of a joined identifier \u2014 a form, file or phone number",
  "alphanumeric": "Part of a token mixing letters and digits",
  "partial-token": "A token that held a figure but did not parse in full",
  "note-header": "A complete financial-statement note heading",
  "note-reference": "A narrative citation linked to a financial-statement note",
  "page-furniture": "On a running header or footer, repeated across pages",
  "phone-context": "Inside a phone number",
  "identifier-context": "Follows a label that introduces a reference number",
  "superscript": "Set smaller than the page's text, so a footnote marker",
  "citation-year": "A year reached through a citation, so not a period",
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

export function describeFsNoise(entry: FsNoiseValue): HoverTipContent {
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

/** Describe a note-heading noise span using its canonical note metadata. */
export function describeFsNoteHeader(
  entry: FsNoiseValue,
  note: FsNote,
  header: FsNoteHeader,
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
      { name: "continuation", value: header.continuation ? "Yes" : "No" },
    ],
  };
}

function describeNumber(value: FinancialValue): string {
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

function describeDate(value: FinancialValue): string {
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
 * keeping a value -- only the rules that would have refused it -- so "no
 * suppressor objected" is the whole truth of why a span was published. When the
 * support model lands and records which evidence carried a value, that reason
 * belongs here, and the sentence above it should go.
 */
export function describeFsValue(value: FinancialValue): HoverTipContent {
  const sentences: string[] = [];
  if (value.kind === "date") sentences.push(describeDate(value));
  else if (value.kind === "percent") {
    sentences.push(
      value.normalizedValue
        ? `Read as ${value.normalizedValue} percent.`
        : "No percentage could be derived from this text.",
    );
  } else sentences.push(describeNumber(value));
  sentences.push("Kept because no suppressor objected to it.");

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
