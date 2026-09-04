import assert from "node:assert/strict";
import test from "node:test";
import { TableStructureCache } from "../src/services/table-structure-cache.ts";

function table(id: string, bounds: { x: number; y: number; width: number; height: number }, confidence = 0.9) {
  return { id, bounds, confidence, evidence: "mixed", columns: [], rows: [], header: null };
}

function cacheOf(tables: unknown[], pageIndex = 2): TableStructureCache {
  const structure = { version: 1, coordinateSpace: "normalized", pages: [{ pageIndex, tables }] };
  return new TableStructureCache(async () => structure as never);
}

test("indexes tables by PDF and page", async () => {
  const cache = cacheOf([
    table("outer", { x: 0.1, y: 0.1, width: 0.8, height: 0.8 }),
    table("inner", { x: 0.2, y: 0.2, width: 0.3, height: 0.3 }),
  ]);
  await cache.build("pdf-1", "encoded");

  assert.equal(cache.tablesOnPage("pdf-1", 2).length, 2);
  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.25, y: 0.25, width: 0.05, height: 0.05 })?.id, "inner");
  assert.equal(cache.tableAt("pdf-1", 1, { x: 0, y: 0, width: 1, height: 1 }), null);
});

test("a selection inside a nested table picks the nested one", async () => {
  const cache = cacheOf([
    table("outer", { x: 0.1, y: 0.1, width: 0.8, height: 0.8 }),
    table("inner", { x: 0.2, y: 0.2, width: 0.3, height: 0.3 }),
  ]);
  await cache.build("pdf-1", "encoded");

  // Fully inside both: the inner table covers the selection just as well and is
  // the more specific answer.
  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.21, y: 0.21, width: 0.08, height: 0.08 })?.id, "inner");
  // Mostly outside the inner one: the outer table explains more of it.
  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.45, y: 0.45, width: 0.4, height: 0.4 })?.id, "outer");
});

test("a selection straddling two adjacent tables picks the one it covers most", async () => {
  const cache = cacheOf([
    table("upper", { x: 0.1, y: 0.10, width: 0.8, height: 0.20 }),
    table("lower", { x: 0.1, y: 0.32, width: 0.8, height: 0.20 }),
  ]);
  await cache.build("pdf-1", "encoded");

  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.2, y: 0.22, width: 0.4, height: 0.11 })?.id, "upper");
  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.2, y: 0.28, width: 0.4, height: 0.16 })?.id, "lower");
});

test("a selection starting above a table still resolves to it", async () => {
  const cache = cacheOf([table("body", { x: 0.1, y: 0.30, width: 0.8, height: 0.30 })]);
  await cache.build("pdf-1", "encoded");

  // The drag began in the whitespace above the table, so its centre sits outside
  // the bounds; overlap still identifies it.
  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.2, y: 0.20, width: 0.4, height: 0.18 })?.id, "body");
});

test("equally covered candidates are resolved by confidence, then deterministically", async () => {
  const bounds = { x: 0.1, y: 0.1, width: 0.4, height: 0.4 };
  const cache = cacheOf([table("b-low", bounds, 0.55), table("a-high", bounds, 0.95)]);
  await cache.build("pdf-1", "encoded");
  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.2, y: 0.2, width: 0.1, height: 0.1 })?.id, "a-high");

  const tie = cacheOf([table("b", bounds, 0.9), table("a", bounds, 0.9)]);
  await tie.build("pdf-2", "encoded");
  assert.equal(tie.tableAt("pdf-2", 2, { x: 0.2, y: 0.2, width: 0.1, height: 0.1 })?.id, "a");
});

test("a selection that only grazes a table belongs to no table", async () => {
  const cache = cacheOf([table("body", { x: 0.5, y: 0.5, width: 0.4, height: 0.4 })]);
  await cache.build("pdf-1", "encoded");

  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.1, y: 0.1, width: 0.405, height: 0.405 }), null);
});

test("a click resolves to the table under it", async () => {
  const cache = cacheOf([table("body", { x: 0.1, y: 0.1, width: 0.4, height: 0.4 })]);
  await cache.build("pdf-1", "encoded");

  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.2, y: 0.2, width: 0, height: 0 })?.id, "body");
  assert.equal(cache.tableAt("pdf-1", 2, { x: 0.8, y: 0.8, width: 0, height: 0 }), null);
});

test("ignores an invalid optional table model", async () => {
  const cache = new TableStructureCache(async () => {
    throw new Error("invalid gzip payload");
  });

  await assert.doesNotReject(cache.build("pdf-1", "broken"));
  assert.deepEqual(cache.tablesOnPage("pdf-1", 0), []);
});

test("counts the tables in a document across its pages", async () => {
  const structure = {
    version: 1,
    coordinateSpace: "normalized",
    pages: [
      { pageIndex: 0, tables: [table("a", { x: 0.1, y: 0.1, width: 0.5, height: 0.2 })] },
      { pageIndex: 1, tables: [] },
      {
        pageIndex: 2,
        tables: [
          table("b", { x: 0.1, y: 0.1, width: 0.5, height: 0.2 }),
          table("c", { x: 0.1, y: 0.4, width: 0.5, height: 0.2 }),
        ],
      },
    ],
  };
  const cache = new TableStructureCache(async () => structure as never);
  await cache.build("pdf-1", "encoded");

  // The indicator shows one number for the document, so pages without tables
  // must not be miscounted and an unknown document must read zero rather than
  // hiding an error behind a plausible number.
  assert.equal(cache.tableCount("pdf-1"), 3);
  assert.equal(cache.tableCount("pdf-unknown"), 0);

  cache.clearPdf("pdf-1");
  assert.equal(cache.tableCount("pdf-1"), 0);
});

test("a document with no stored structure counts zero", async () => {
  const cache = cacheOf([]);
  await cache.build("pdf-1", undefined);

  assert.equal(cache.tableCount("pdf-1"), 0);
});
