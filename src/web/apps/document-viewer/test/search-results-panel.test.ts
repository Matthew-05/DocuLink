import assert from "node:assert/strict";
import test from "node:test";

import {
  getSearchResultsSummary,
  groupSearchResultsInOrder,
} from "../src/components/toolbar/search-results-panel.ts";
import type { SearchMatch } from "../src/types/index.ts";

function match(
  id: string,
  pdfId: string,
  pageIndex: number,
  exactMatch: boolean,
): SearchMatch {
  return {
    id,
    pdfId,
    pdfName: `${pdfId}.pdf`,
    pageIndex,
    exactMatch,
    contextText: "11",
    matchInContext: { start: 0, end: 2 },
    highlightRect: { x: 0, y: 0, width: 1, height: 1 },
  };
}

test("shows an empty state after a completed search finds no matches", () => {
  assert.equal(getSearchResultsSummary(0, false), "No results");
});

test("keeps the searching state while more pages remain", () => {
  assert.equal(getSearchResultsSummary(0, true), "Searching...");
});

test("formats completed and partial result counts", () => {
  assert.equal(getSearchResultsSummary(1, false), "1 result");
  assert.equal(getSearchResultsSummary(2, false), "2 results");
  assert.equal(getSearchResultsSummary(2, true), "2+ results");
});

test("preserves exact-first order instead of re-sorting results by page", () => {
  const ranked = [
    match("exact-page-3", "apple", 2, true),
    match("exact-page-4", "apple", 3, true),
    match("partial-page-1", "apple", 0, false),
  ];

  const displayed = groupSearchResultsInOrder(ranked).flatMap((group) => group.matches);
  assert.deepEqual(displayed.map((result) => result.id), [
    "exact-page-3",
    "exact-page-4",
    "partial-page-1",
  ]);
});

test("preserves global exact-first order when document headings repeat", () => {
  const ranked = [
    match("exact-a", "a", 2, true),
    match("exact-b", "b", 4, true),
    match("partial-a", "a", 0, false),
  ];

  const groups = groupSearchResultsInOrder(ranked);
  assert.deepEqual(groups.map((group) => group.pdfId), ["a", "b", "a"]);
  assert.deepEqual(
    groups.flatMap((group) => group.matches).map((result) => result.exactMatch),
    [true, true, false],
  );
});
