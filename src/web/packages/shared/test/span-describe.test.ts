import assert from "node:assert/strict";
import test from "node:test";
import {
  describeItemHeader,
  describeItemTocEntry,
  describeNoise,
  describeNoteHeader,
  describeReference,
  describeValue,
  type SpanTipContext,
} from "../src/span-describe.ts";

const bounds = { x: 0.1, y: 0.2, width: 0.3, height: 0.04 };
const context: SpanTipContext = { pageIndex: 3, pageContext: { scale: 1000000 } };

/** The four facts every tip ends with, whatever it describes. */
function provenance(valueType: string, clickable: string, id: string, page = "4") {
  return [
    { name: "value type", value: valueType },
    { name: "click target", value: clickable },
    { name: "page", value: page },
    { name: "id", value: id },
  ];
}

test("note-header tooltips list note metadata and continuation status", () => {
  const content = describeNoteHeader(
    { id: "str-note-7", kind: "note-header", clickable: false, text: "7", bounds },
    {
      id: "note-7",
      identifier: "7",
      description: "Income Taxes",
      headers: [
        { id: "note-7-h0", pageIndex: 2, text: "Note 7 — Income Taxes", bounds, continuation: false },
        { id: "note-7-h1", pageIndex: 4, text: "Note 7 — Income Taxes (continued)", bounds, continuation: true },
      ],
    },
    {
      id: "note-7-h1",
      pageIndex: 4,
      text: "Note 7 — Income Taxes (continued)",
      bounds,
      continuation: true,
    },
    context,
  );

  assert.deepEqual(content.fields, [
    { name: "note number", value: "7" },
    { name: "note description", value: "Income Taxes" },
    { name: "heading", value: "Note 7 — Income Taxes (continued)" },
    { name: "continuation", value: "Yes" },
    { name: "occurrences", value: "2" },
    ...provenance("note-header", "No", "str-note-7"),
  ]);
  assert.equal(content.label, "structure");
  assert.equal(content.variant, "structure");
});

test("note-header tooltips make absent descriptions and non-continuations explicit", () => {
  const content = describeNoteHeader(
    { id: "str-note-iv", kind: "note-header", clickable: false, text: "IV", bounds },
    {
      id: "note-IV",
      identifier: "IV",
      description: "",
      headers: [{ id: "note-IV-h0", pageIndex: 1, text: "Note IV", bounds, continuation: false }],
    },
    { id: "note-IV-h0", pageIndex: 1, text: "Note IV", bounds, continuation: false },
    context,
  );

  assert.deepEqual(content.fields, [
    { name: "note number", value: "IV" },
    { name: "note description", value: "Not provided" },
    { name: "heading", value: "Note IV" },
    { name: "continuation", value: "No" },
    { name: "occurrences", value: "1" },
    ...provenance("note-header", "No", "str-note-iv"),
  ]);
});

test("item-header tooltips name the part and where the description came from", () => {
  const content = describeItemHeader(
    { id: "str-item-1a", kind: "item-header", clickable: false, text: "1A", bounds },
    {
      id: "item-1A",
      identifier: "1A",
      part: "I",
      description: "Risk Factors",
      descriptionSource: "toc",
      headers: [],
      tocEntries: [],
    },
    { id: "ih0", pageIndex: 7, text: "Item 1A. Risk Factors", bounds, continuation: false },
    context,
  );

  assert.deepEqual(content.fields, [
    { name: "item number", value: "1A" },
    { name: "item description", value: "Risk Factors" },
    { name: "part", value: "I" },
    { name: "description from", value: "The table of contents" },
    { name: "heading", value: "Item 1A. Risk Factors" },
    { name: "continuation", value: "No" },
    ...provenance("item-header", "No", "str-item-1a"),
  ]);
});

test("item-header tooltips omit the part when the document groups nothing", () => {
  const content = describeItemHeader(
    { id: "str-item-6", kind: "item-header", clickable: false, text: "6", bounds },
    {
      id: "item-6",
      identifier: "6",
      description: "[Reserved]",
      descriptionSource: "heading",
      headers: [],
      tocEntries: [],
    },
    { id: "ih1", pageIndex: 22, text: "Item 6. [Reserved]", bounds, continuation: false },
    context,
  );

  assert.deepEqual(content.fields, [
    { name: "item number", value: "6" },
    { name: "item description", value: "[Reserved]" },
    { name: "description from", value: "A heading in the body" },
    { name: "heading", value: "Item 6. [Reserved]" },
    { name: "continuation", value: "No" },
    ...provenance("item-header", "No", "str-item-6"),
  ]);
});

test("contents-row tooltips say which detector read the row", () => {
  const item = {
    id: "item-7",
    identifier: "7",
    description: "Management’s Discussion and Analysis",
    descriptionSource: "toc" as const,
    headers: [],
    tocEntries: [],
  };
  const span = {
    id: "str-toc-7",
    kind: "item-toc-entry" as const,
    clickable: false,
    text: "21",
    bounds,
  };

  const fromTable = describeItemTocEntry(span, item, {
    id: "it0", pageIndex: 2, text: "Item 7. … 21", bounds, corroborated: true, printedPage: "21",
  });
  const fromText = describeItemTocEntry(span, item, {
    id: "it1", pageIndex: 2, text: "Item 7. …", bounds, corroborated: false,
  });

  assert.deepEqual(fromTable.fields?.slice(2, 4), [
    { name: "points at page", value: "21" },
    { name: "read from", value: "A detected table" },
  ]);
  assert.deepEqual(fromText.fields?.slice(2, 4), [
    { name: "points at page", value: "Not printed" },
    { name: "read from", value: "Text geometry" },
  ]);
});

test("a value tip reports its reading, its page's scale, and that it is a target", () => {
  const content = describeValue(
    {
      id: "val-1",
      kind: "number",
      text: "1,234",
      bounds,
      clickable: true,
      confidence: 0.94,
      normalizedValue: "1234",
      currency: "USD",
    },
    context,
  );

  assert.match(content.detail, /Read as 1234, in USD\./);
  assert.equal(content.label, "value");
  assert.equal(content.variant, "value");
  assert.deepEqual(content.fields, [
    { name: "normalized", value: "1234" },
    // The span itself prints no symbol, so the currency came from its page.
    { name: "currency", value: "USD (inherited, not printed here)" },
    // Scale is what the page's caption claims and is deliberately not folded
    // into normalizedValue; saying so is the whole point of surfacing it.
    { name: "page states", value: "in millions (not applied)" },
    { name: "confidence", value: "0.94 (shape, not certainty)" },
    ...provenance("number", "Yes", "val-1"),
  ]);
});

test("a magnitude is reported as already applied, and a printed currency is not called inherited", () => {
  const content = describeValue({
    id: "val-2",
    kind: "number",
    text: "$1.2 billion",
    bounds,
    clickable: true,
    confidence: 0.99,
    normalizedValue: "1200000000",
    magnitude: 1000000000,
    currency: "USD",
  });

  assert.match(content.detail, /with the billions modifier already applied/);
  assert.deepEqual(content.fields?.slice(0, 3), [
    { name: "normalized", value: "1200000000" },
    { name: "currency", value: "USD" },
    { name: "magnitude", value: "x1000000000 (billions), applied" },
  ]);
});

test("an ambiguous date says no reading was made rather than guessing one", () => {
  const content = describeValue({
    id: "val-3",
    kind: "date",
    text: "03/04/2025",
    bounds,
    clickable: true,
    confidence: 0.98,
    datePrecision: "day",
    dateOrder: "ambiguous",
  });

  assert.match(content.detail, /no date was derived/);
  assert.equal(content.fields?.some((field) => field.name === "normalized"), false);
});

test("a reference tip claims no value and reports clickability", () => {
  const content = describeReference(
    { id: "ref-1", kind: "identifier", clickable: true, text: "BPXINV-00550", bounds },
    context,
  );

  assert.match(content.detail, /no value read from it/);
  assert.equal(content.label, "reference");
  assert.equal(content.variant, "reference");
  assert.deepEqual(content.fields, provenance("identifier", "Yes", "ref-1"));
});

test("a noise tip names its category and reports its recognized value type", () => {
  const content = describeNoise(
    {
      id: "noi-1",
      kind: "number",
      text: "12x",
      bounds,
      clickable: false,
      reason: "partial-token",
    },
    context,
  );

  assert.equal(content.label, "noise");
  assert.equal(content.variant, "noise");
  assert.deepEqual(content.fields, [
    { name: "refusal reason", value: "partial-token" },
    ...provenance("number", "No", "noi-1"),
  ]);
});
