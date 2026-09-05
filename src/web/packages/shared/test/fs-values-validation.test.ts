import assert from "node:assert/strict";
import test from "node:test";
import { parseFsValues } from "../src/fs-values-decoder.ts";

test("validates fs-values and drops malformed individual values", () => {
  const parsed = parseFsValues({
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: "test",
    documentContext: { currency: "USD" },
    pages: [{
      pageIndex: 0,
      context: { scale: 1000000 },
      values: [
        { id: "good", kind: "number", text: "$1,000", normalizedValue: "1000", bounds: { x: 0.1, y: 0.2, width: 0.1, height: 0.02 }, confidence: 0.9 },
        { id: "bad", kind: "number", text: "bad", bounds: { x: 2, y: 0, width: 1, height: 1 }, confidence: 0.9 },
      ],
    }],
  });
  assert.equal(parsed.pages[0]?.values.length, 1);
  assert.equal(parsed.documentContext.currency, "USD");
});

test("validates magnitude values with split click segments", () => {
  const parsed = parseFsValues({
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: "test",
    documentContext: {},
    pages: [{
      pageIndex: 0,
      context: {},
      values: [{
        id: "wrapped",
        kind: "number",
        text: "$1 million",
        normalizedValue: "1000000",
        magnitude: 1000000,
        bounds: { x: 0.8, y: 0.9, width: 0.1, height: 0.02 },
        confidence: 0.9,
        segments: [
          { pageIndex: 0, text: "$1", bounds: { x: 0.8, y: 0.9, width: 0.1, height: 0.02 } },
          { pageIndex: 1, text: "million", bounds: { x: 0.1, y: 0.1, width: 0.2, height: 0.02 } },
        ],
      }],
    }],
  });
  assert.equal(parsed.pages[0]?.values[0]?.magnitude, 1000000);
  assert.equal(parsed.pages[0]?.values[0]?.segments?.[1]?.pageIndex, 1);
});

test("parses page noise and drops entries with an unknown reason", () => {
  const parsed = parseFsValues({
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: "test",
    documentContext: {},
    pages: [{
      pageIndex: 0,
      context: {},
      values: [],
      noise: [
        { id: "n0", kind: "number", text: "10", bounds: { x: 0.1, y: 0.1, width: 0.02, height: 0.02 }, reason: "identifier" },
        { id: "n1", kind: "date", text: "1934", bounds: { x: 0.2, y: 0.1, width: 0.04, height: 0.02 }, reason: "not-a-rule" },
        { id: "n2", kind: "number", text: "7", bounds: { x: 2, y: 0, width: 1, height: 1 }, reason: "page-furniture" },
      ],
    }],
  });
  assert.deepEqual(parsed.pages[0]?.noise.map((entry) => entry.id), ["n0"]);
  assert.equal(parsed.pages[0]?.noise[0]?.reason, "identifier");
});

test("defaults noise to empty when a page omits it", () => {
  const parsed = parseFsValues({
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: "test",
    documentContext: {},
    pages: [{ pageIndex: 0, context: {}, values: [] }],
  });
  assert.deepEqual(parsed.pages[0]?.noise, []);
});
