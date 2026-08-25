import assert from "node:assert/strict";
import test from "node:test";

import { getSearchResultsSummary } from "../src/components/toolbar/search-results-panel.ts";

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
