import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { NOISE_REASONS, REFERENCE_KINDS, parseDocumentValues } from "../src/document-values-decoder.ts";

const bounds = { x: 0.1, y: 0.2, width: 0.1, height: 0.02 };

function model(page: Record<string, unknown>): Record<string, unknown> {
  return {
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: "test",
    documentContext: { currency: "USD" },
    pages: [page],
  };
}

test("validates values and drops malformed individual ones", () => {
  const parsed = parseDocumentValues(model({
    pageIndex: 0,
    context: { scale: 1000000 },
    values: [
      { id: "val-good", kind: "number", text: "$1,000", normalizedValue: "1000", bounds, confidence: 0.9 },
      { id: "val-bad", kind: "number", text: "bad", bounds: { x: 2, y: 0, width: 1, height: 1 }, confidence: 0.9 },
    ],
  }));
  assert.equal(parsed.pages[0]?.values.length, 1);
  assert.equal(parsed.documentContext.currency, "USD");
});

test("validates magnitude values with split click segments", () => {
  const parsed = parseDocumentValues(model({
    pageIndex: 0,
    context: {},
    values: [{
      id: "val-wrapped",
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
  }));
  assert.equal(parsed.pages[0]?.values[0]?.magnitude, 1000000);
  assert.equal(parsed.pages[0]?.values[0]?.segments?.[1]?.pageIndex, 1);
});

test("parses references and drops ones with an unknown kind", () => {
  const parsed = parseDocumentValues(model({
    pageIndex: 0,
    context: {},
    values: [],
    references: [
      { id: "ref-0", kind: "identifier", text: "BPXINV-00550", bounds },
      { id: "ref-1", kind: "note", text: "Note 7", bounds },
      { id: "ref-2", kind: "not-a-kind", text: "?", bounds },
    ],
  }));
  assert.deepEqual(parsed.pages[0]?.references.map((entry) => entry.id), ["ref-0", "ref-1"]);
});

test("parses page noise and drops entries with an unknown reason", () => {
  const parsed = parseDocumentValues(model({
    pageIndex: 0,
    context: {},
    values: [],
    noise: [
      { id: "noi-0", kind: "number", text: "12", bounds, reason: "note-header" },
      { id: "noi-1", kind: "date", text: "1934", bounds, reason: "not-a-rule" },
      { id: "noi-2", kind: "number", text: "7", bounds: { x: 2, y: 0, width: 1, height: 1 }, reason: "page-furniture" },
    ],
  }));
  assert.deepEqual(parsed.pages[0]?.noise.map((entry) => entry.id), ["noi-0"]);
  assert.equal(parsed.pages[0]?.noise[0]?.reason, "note-header");
});

test("defaults references and noise to empty when a page omits them", () => {
  const parsed = parseDocumentValues(model({ pageIndex: 0, context: {}, values: [] }));
  assert.deepEqual(parsed.pages[0]?.references, []);
  assert.deepEqual(parsed.pages[0]?.noise, []);
});

/**
 * The decoder drops a span whose reason or kind it does not recognize, and it
 * does so silently -- which is how a reason added to the detector and the
 * contract, but not here, made a whole overlay layer render nothing. The
 * contract is the source of truth, so read it rather than restating it.
 */
test("the decoder accepts exactly the reasons and kinds the contract defines", async () => {
  const contract = JSON.parse(
    await readFile(
      new URL("../../../../../contracts/document-values-v1.json", import.meta.url),
      "utf8",
    ),
  ) as {
    definitions: {
      Noise: { properties: { reason: { enum: string[] } } };
      Reference: { properties: { kind: { enum: string[] } } };
    };
  };
  assert.deepEqual(
    [...NOISE_REASONS].sort(),
    [...contract.definitions.Noise.properties.reason.enum].sort(),
  );
  assert.deepEqual(
    [...REFERENCE_KINDS].sort(),
    [...contract.definitions.Reference.properties.kind.enum].sort(),
  );
});

test("every contract reason and kind survives parsing", () => {
  const parsed = parseDocumentValues(model({
    pageIndex: 0,
    context: {},
    values: [],
    references: REFERENCE_KINDS.map((kind, index) => ({
      id: `ref-${index}`,
      kind,
      text: kind,
      bounds,
    })),
    noise: NOISE_REASONS.map((reason, index) => ({
      id: `noi-${index}`,
      kind: "text",
      text: reason,
      bounds,
      reason,
    })),
  }));
  assert.deepEqual(parsed.pages[0]?.references.map((entry) => entry.kind), [...REFERENCE_KINDS]);
  assert.deepEqual(parsed.pages[0]?.noise.map((entry) => entry.reason), [...NOISE_REASONS]);
});
