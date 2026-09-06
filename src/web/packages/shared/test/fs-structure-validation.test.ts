import assert from "node:assert/strict";
import { registerHooks } from "node:module";
import test from "node:test";

// The decoder shares its primitives with document-values-decoder, and imports
// them by the ".js" specifier the bundler needs. Map it back to source, as the
// other tests over cross-importing modules do.
const structureUrl = new URL("../src/fs-structure-decoder.ts", import.meta.url).href;
const valuesUrl = new URL("../src/document-values-decoder.ts", import.meta.url).href;

registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier === "./document-values-decoder.js" && context.parentURL === structureUrl) {
      return { url: valuesUrl, shortCircuit: true };
    }
    return nextResolve(specifier, context);
  },
});

const { parseFsStructure } = await import(structureUrl) as typeof import("../src/fs-structure-decoder.ts");

const bounds = { x: 0.1, y: 0.1, width: 0.2, height: 0.02 };

const apparatus = {
  notes: { searched: true, found: 1 },
  items: { searched: true, found: 1 },
  contents: { searched: true, found: 1 },
  parts: { searched: true, found: 1 },
};

test("parses canonical notes, continuation headers, and resolved citations", () => {
  const parsed = parseFsStructure({
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: "test",
    documentClass: "statement",
    apparatus,
    notes: [{
      id: "note-IV",
      identifier: "IV",
      description: "Income Taxes",
      headers: [
        { id: "note-IV-h0", pageIndex: 2, text: "Note IV — Income Taxes", bounds, continuation: false },
        { id: "note-IV-h1", pageIndex: 3, text: "Note IV (continued)", bounds, continuation: true },
      ],
    }],
    items: [],
    itemReferences: [],
    noteReferences: [{
      id: "noteref-0-0",
      spanId: "ref-00112233445566aa",
      noteId: "note-IV",
      identifier: "IV",
      description: "Income Taxes",
      pageIndex: 1,
      descriptionPresent: false,
    }],
  });

  assert.equal(parsed.documentClass, "statement");
  assert.equal(parsed.notes[0]?.headers[1]?.continuation, true);
  assert.equal(parsed.noteReferences[0]?.description, "Income Taxes");
  assert.equal(parsed.noteReferences[0]?.spanId, "ref-00112233445566aa");
});

test("parses filing items, contents rows, and drops a placeless item with its citation", () => {
  const parsed = parseFsStructure({
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: "test",
    documentClass: "filing",
    apparatus,
    notes: [],
    noteReferences: [],
    items: [
      {
        id: "item-1A",
        identifier: "1A",
        part: "I",
        description: "Risk Factors",
        descriptionSource: "toc",
        headers: [{ id: "item-1A-h0", pageIndex: 7, text: "Item 1A. Risk Factors", bounds, continuation: false }],
        tocEntries: [{
          id: "item-1A-t0",
          pageIndex: 2,
          text: "Item 1A. Risk Factors 5",
          bounds,
          corroborated: true,
          printedPage: "5",
          segments: [
            { pageIndex: 2, text: "Item 1A.", bounds },
            { pageIndex: 2, text: "Risk Factors", bounds },
          ],
        }],
      },
      {
        id: "item-9",
        identifier: "9",
        description: "Nowhere",
        descriptionSource: "none",
        headers: [],
        tocEntries: [],
      },
    ],
    itemReferences: [
      { id: "itemref-0", spanId: "ref-aabbccddeeff0011", itemId: "item-1A", identifier: "1A", part: "I", description: "Risk Factors", pageIndex: 19, descriptionPresent: false },
      { id: "itemref-1", spanId: "ref-1122334455667788", itemId: "item-9", identifier: "9", description: "Nowhere", pageIndex: 19, descriptionPresent: false },
    ],
  });

  assert.equal(parsed.items.length, 1);
  assert.equal(parsed.items[0]?.tocEntries[0]?.printedPage, "5");
  assert.equal(parsed.items[0]?.part, "I");
  // An item with no heading and no contents row has no place on any page, and
  // the citation pointing at it goes with it.
  assert.equal(parsed.itemReferences.length, 1);
  assert.equal(parsed.itemReferences[0]?.itemId, "item-1A");
});

/**
 * The distinction the check suite stands on: a compilation that has no items
 * is not a filing whose item detection failed. `searched` is what says which.
 */
test("apparatus records what was looked for as well as what was found", () => {
  const parsed = parseFsStructure({
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: "test",
    documentClass: "statement",
    apparatus: {
      notes: { searched: true, found: 14 },
      items: { searched: true, found: 0 },
      contents: { searched: true, found: 0 },
      parts: { searched: true, found: 0 },
    },
    notes: [],
    noteReferences: [],
    items: [],
    itemReferences: [],
  });
  assert.equal(parsed.apparatus.items.searched, true);
  assert.equal(parsed.apparatus.items.found, 0);
});

test("rejects an unknown document class", () => {
  assert.throws(() => parseFsStructure({
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: "test",
    documentClass: "prospectus",
    apparatus,
    notes: [],
    noteReferences: [],
    items: [],
    itemReferences: [],
  }));
});
