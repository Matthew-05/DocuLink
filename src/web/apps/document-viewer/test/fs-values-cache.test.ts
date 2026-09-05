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
