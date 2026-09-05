import assert from "node:assert/strict";
import test from "node:test";
import { describeFsNoteHeader } from "../src/fs-values-describe.ts";

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
