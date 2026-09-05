import assert from "node:assert/strict";
import test from "node:test";
import {
  describeFsItemHeader,
  describeFsItemTocEntry,
  describeFsNoteHeader,
} from "../src/fs-values-describe.ts";

const bounds = { x: 0.1, y: 0.2, width: 0.3, height: 0.04 };

test("note-header tooltips list note metadata and continuation status", () => {
  const content = describeFsNoteHeader(
    {
      id: "noise-note-7",
      kind: "text",
      text: "Note 7 — Income Taxes (continued)",
      bounds,
      reason: "note-header",
    },
    {
      id: "note-7",
      identifier: "7",
      description: "Income Taxes",
      headers: [],
    },
    {
      id: "header-7-continued",
      pageIndex: 4,
      text: "Note 7 — Income Taxes (continued)",
      bounds,
      continuation: true,
    },
  );

  assert.deepEqual(content.fields, [
    { name: "note number", value: "7" },
    { name: "note description", value: "Income Taxes" },
    { name: "continuation", value: "Yes" },
  ]);
});

test("note-header tooltips make absent descriptions and non-continuations explicit", () => {
  const content = describeFsNoteHeader(
    { id: "noise-note-iv", kind: "text", text: "Note IV", bounds, reason: "note-header" },
    { id: "note-iv", identifier: "IV", description: "", headers: [] },
    { id: "header-iv", pageIndex: 1, text: "Note IV", bounds, continuation: false },
  );

  assert.deepEqual(content.fields, [
    { name: "note number", value: "IV" },
    { name: "note description", value: "Not provided" },
    { name: "continuation", value: "No" },
  ]);
});

test("item-header tooltips name the part and where the description came from", () => {
  const content = describeFsItemHeader(
    { id: "noise-item-1a", kind: "text", text: "Item 1A. Risk Factors", bounds, reason: "item-header" },
    {
      id: "fs-item-1",
      identifier: "1A",
      part: "I",
      description: "Risk Factors",
      descriptionSource: "toc",
      headers: [],
      tocEntries: [],
    },
    { id: "ih0", pageIndex: 7, text: "Item 1A. Risk Factors", bounds, continuation: false },
  );

  assert.deepEqual(content.fields, [
    { name: "item number", value: "1A" },
    { name: "item description", value: "Risk Factors" },
    { name: "part", value: "I" },
    { name: "description from", value: "The table of contents" },
    { name: "continuation", value: "No" },
  ]);
});

test("item-header tooltips omit the part when the document groups nothing", () => {
  const content = describeFsItemHeader(
    { id: "noise-item-6", kind: "text", text: "Item 6. [Reserved]", bounds, reason: "item-header" },
    {
      id: "fs-item-6",
      identifier: "6",
      description: "[Reserved]",
      descriptionSource: "heading",
      headers: [],
      tocEntries: [],
    },
    { id: "ih1", pageIndex: 22, text: "Item 6. [Reserved]", bounds, continuation: false },
  );

  assert.deepEqual(content.fields, [
    { name: "item number", value: "6" },
    { name: "item description", value: "[Reserved]" },
    { name: "description from", value: "A heading in the body" },
    { name: "continuation", value: "No" },
  ]);
});

test("contents-row tooltips say which detector read the row", () => {
  const item = {
    id: "fs-item-7",
    identifier: "7",
    description: "Management’s Discussion and Analysis",
    descriptionSource: "toc" as const,
    headers: [],
    tocEntries: [],
  };
  const noise = {
    id: "noise-toc-7",
    kind: "text" as const,
    text: "Item 7. Management’s Discussion and Analysis 21",
    bounds,
    reason: "item-toc-entry" as const,
  };

  const fromTable = describeFsItemTocEntry(noise, item, {
    id: "it0", pageIndex: 2, text: noise.text, bounds, corroborated: true, printedPage: "21",
  });
  const fromText = describeFsItemTocEntry(noise, item, {
    id: "it1", pageIndex: 2, text: noise.text, bounds, corroborated: false,
  });

  assert.deepEqual(fromTable.fields?.slice(-2), [
    { name: "points at page", value: "21" },
    { name: "read from", value: "A detected table" },
  ]);
  assert.deepEqual(fromText.fields?.slice(-2), [
    { name: "points at page", value: "Not printed" },
    { name: "read from", value: "Text geometry" },
  ]);
});
