import assert from "node:assert/strict";
import test from "node:test";
import { detectFsValuesFromEntries } from "../src/fs-values-detector.ts";
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

test("fallback detects dates before their component numbers", () => {
  const text = "December 31, 2025  $ 1,200  5%";
  const entries = [...text].map((char, index) => ({
    char,
    normLeft: index * 0.01,
    normTop: 0.1,
    normRight: (index + 1) * 0.01,
    normBottom: 0.12,
    lineIndex: 0,
    itemIndex: index,
    spacesPrecomputed: true,
  }));
  const page = detectFsValuesFromEntries(0, entries);
  assert.deepEqual(page.values.map((value) => value.kind), ["date", "number", "percent"]);
  assert.equal(page.values[0]?.text, "December 31, 2025");
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

test("fallback calculates same-line magnitude values", () => {
  const text = "$1.0 million";
  const entries = [...text].map((char, index) => ({
    char,
    normLeft: index * 0.01,
    normTop: 0.1,
    normRight: (index + 1) * 0.01,
    normBottom: 0.12,
    lineIndex: 0,
    itemIndex: index,
    spacesPrecomputed: true,
  }));
  const value = detectFsValuesFromEntries(0, entries).values[0];
  assert.equal(value?.text, "$1.0 million");
  assert.equal(value?.normalizedValue, "1000000");
  assert.equal(value?.magnitude, 1000000);
});
