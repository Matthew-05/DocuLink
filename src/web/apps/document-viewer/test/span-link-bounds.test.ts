import assert from "node:assert/strict";
import test from "node:test";

import { getSpanLinkTarget, getValueLinkBounds } from "../src/components/viewer/span-link-bounds.ts";

test("uses the union of every segment for a wrapped date on one page", () => {
  const clickedBounds = { x: 0.2, y: 0.1, width: 0.3, height: 0.04 };
  const bounds = getValueLinkBounds({
    id: "date-1",
    kind: "date",
    text: "September 27, 2025",
    bounds: clickedBounds,
    confidence: 0.95,
    segments: [
      { pageIndex: 2, text: "September 27,", bounds: clickedBounds },
      { pageIndex: 2, text: "2025", bounds: { x: 0.32, y: 0.15, width: 0.1, height: 0.04 } },
    ],
  }, 2);

  assert.deepEqual(bounds, { x: 0.2, y: 0.1, width: 0.3, height: 0.09 });
});

test("keeps the clicked segment bounds for a wrapped number modifier", () => {
  const clickedBounds = { x: 0.1, y: 0.1, width: 0.15, height: 0.03 };
  const bounds = getValueLinkBounds({
    id: "number-1",
    kind: "number",
    text: "$1 million",
    bounds: clickedBounds,
    confidence: 0.95,
    segments: [
      { pageIndex: 0, text: "$1", bounds: clickedBounds },
      { pageIndex: 1, text: "million", bounds: { x: 0.1, y: 0.1, width: 0.2, height: 0.03 } },
    ],
  }, 0);

  assert.equal(bounds, clickedBounds);
});

test("keeps the clicked bounds when date segments do not share a page", () => {
  const clickedBounds = { x: 0.7, y: 0.9, width: 0.2, height: 0.03 };
  const bounds = getValueLinkBounds({
    id: "date-2",
    kind: "date",
    text: "September 27, 2025",
    bounds: clickedBounds,
    confidence: 0.95,
    segments: [
      { pageIndex: 0, text: "September 27,", bounds: clickedBounds },
      { pageIndex: 1, text: "2025", bounds: { x: 0.3, y: 0.1, width: 0.1, height: 0.03 } },
    ],
  }, 0);

  assert.equal(bounds, clickedBounds);
});

test("a reference and a structure span link as they stand", () => {
  const bounds = { x: 0.2, y: 0.3, width: 0.1, height: 0.02 };
  assert.deepEqual(
    getSpanLinkTarget(
      { category: "reference", reference: { id: "ref-1", kind: "identifier", text: "A-1", bounds, clickable: true } },
      0,
    ),
    { rect: bounds, text: "A-1" },
  );
  assert.deepEqual(
    getSpanLinkTarget(
      { category: "structure", structure: { id: "str-1", kind: "list-marker", text: "3", bounds, clickable: true } },
      0,
    ),
    { rect: bounds, text: "3" },
  );
});
