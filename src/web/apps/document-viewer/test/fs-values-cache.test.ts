import assert from "node:assert/strict";
import test from "node:test";
import { FsValuesCache } from "../src/services/fs-values-cache.ts";

test("indexes a stored model by document and page", async () => {
  const model = {
    version: 1, coordinateSpace: "normalized", detectorVersion: "test", documentContext: {},
    pages: [{ pageIndex: 2, context: {}, values: [{ id: "n", kind: "number", text: "100", bounds: { x: 0.1, y: 0.1, width: 0.1, height: 0.02 }, confidence: 0.9 }] }],
  };
  const cache = new FsValuesCache(async () => model as never);
  await cache.build("pdf", "encoded");
  assert.equal(cache.valuesOnPage("pdf", 2)[0]?.text, "100");
  assert.equal(cache.valueCount("pdf"), 1);
});

test("does not synthesize values for a document with no analyzed artifact", async () => {
  const cache = new FsValuesCache();
  await cache.build("native-pdf", undefined);
  assert.equal(cache.has("native-pdf"), true);
  assert.equal(cache.valueCount("native-pdf"), 0);
});

test("indexes every segment of one wrapped logical value", async () => {
  const model = {
    version: 1, coordinateSpace: "normalized", detectorVersion: "test", documentContext: {},
    pages: [{ pageIndex: 0, context: {}, values: [{
      id: "n", kind: "number", text: "$1 million", normalizedValue: "1000000", magnitude: 1000000,
      bounds: { x: 0.8, y: 0.9, width: 0.1, height: 0.02 }, confidence: 0.9,
      segments: [
        { pageIndex: 0, text: "$1", bounds: { x: 0.8, y: 0.9, width: 0.1, height: 0.02 } },
        { pageIndex: 1, text: "million", bounds: { x: 0.1, y: 0.1, width: 0.2, height: 0.02 } },
      ],
    }] }],
  };
  const cache = new FsValuesCache(async () => model as never);
  await cache.build("pdf", "encoded");
  assert.equal(cache.valuesOnPage("pdf", 0)[0]?.bounds.x, 0.8);
  assert.equal(cache.valuesOnPage("pdf", 1)[0]?.bounds.x, 0.1);
  assert.equal(cache.valuesOnPage("pdf", 1)[0]?.text, "$1 million");
  assert.equal(cache.valueCount("pdf"), 1);
});

test("keeps refused spans separate from the values they were kept from", async () => {
  const model = {
    version: 1, coordinateSpace: "normalized", detectorVersion: "test", documentContext: {},
    pages: [{
      pageIndex: 0,
      context: {},
      values: [{ id: "v", kind: "number", text: "1,234", bounds: { x: 0.1, y: 0.1, width: 0.1, height: 0.02 }, confidence: 0.94 }],
      noise: [{ id: "n", kind: "number", text: "10", bounds: { x: 0.5, y: 0.1, width: 0.02, height: 0.02 }, reason: "identifier" }],
    }],
  };
  const cache = new FsValuesCache(async () => model as never);
  await cache.build("pdf", "encoded");
  assert.deepEqual(cache.valuesOnPage("pdf", 0).map((value) => value.text), ["1,234"]);
  assert.deepEqual(cache.noiseOnPage("pdf", 0).map((entry) => entry.text), ["10"]);
  assert.equal(cache.valueCount("pdf"), 1);
  assert.equal(cache.noiseCount("pdf"), 1);
});

test("reports no noise for a document with no analyzed artifact", async () => {
  const cache = new FsValuesCache();
  await cache.build("native-pdf", undefined);
  assert.equal(cache.noiseCount("native-pdf"), 0);
  assert.deepEqual(cache.noiseOnPage("native-pdf", 0), []);
});

test("retains the canonical note catalogue and its resolved references", async () => {
  const bounds = { x: 0.1, y: 0.1, width: 0.2, height: 0.02 };
  const model = {
    version: 1, coordinateSpace: "normalized", detectorVersion: "test", documentContext: {},
    notes: [{
      id: "fs-note-0", identifier: "3.1", description: "Revenue Recognition",
      headers: [{ id: "h0", pageIndex: 2, text: "Note 3.1 — Revenue Recognition", bounds, continuation: false }],
    }],
    noteReferences: [{
      id: "r0", noteId: "fs-note-0", identifier: "3.1", description: "Revenue Recognition",
      pageIndex: 1, text: "Note 3.1", bounds, descriptionPresent: false,
    }],
    pages: [{ pageIndex: 1, context: {}, values: [] }],
  };
  const cache = new FsValuesCache(async () => model as never);
  await cache.build("pdf", "encoded");

  assert.equal(cache.notes("pdf")[0]?.description, "Revenue Recognition");
  assert.equal(cache.noteReferences("pdf")[0]?.noteId, "fs-note-0");
});
