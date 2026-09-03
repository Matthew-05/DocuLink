import assert from "node:assert/strict";
import test from "node:test";
import { TableStructureCache } from "../src/services/table-structure-cache.ts";

test("indexes tables by PDF and page and finds the smallest containing table", async () => {
  const structure = {
    version: 1,
    coordinateSpace: "normalized",
    pages: [{
      pageIndex: 2,
      tables: [
        { id: "outer", bounds: { x: 0.1, y: 0.1, width: 0.8, height: 0.8 } },
        { id: "inner", bounds: { x: 0.2, y: 0.2, width: 0.3, height: 0.3 } },
      ],
    }],
  };
  const cache = new TableStructureCache(async () => structure as never);
  await cache.build("pdf-1", "encoded");

  assert.equal(cache.tablesOnPage("pdf-1", 2).length, 2);
  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.25, y: 0.25, width: 0.05, height: 0.05 })?.id, "inner");
  assert.equal(cache.tableAt("pdf-1", 1, { x: 0, y: 0, width: 1, height: 1 }), null);
});

test("ignores an invalid optional table model", async () => {
  const cache = new TableStructureCache(async () => {
    throw new Error("invalid gzip payload");
  });

  await assert.doesNotReject(cache.build("pdf-1", "broken"));
  assert.deepEqual(cache.tablesOnPage("pdf-1", 0), []);
});
