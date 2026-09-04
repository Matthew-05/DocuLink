import assert from "node:assert/strict";

// Imported straight from TypeScript: Node strips the types, so this test needs no
// build step and no transpiler dependency.
import { parseTableStructure } from "../src/table-structure-decoder.ts";

const row = (y0, y1, kind = "body") => ({
  y0,
  y1,
  kind,
  textLines: [{ y0, y1 }],
  merged: false,
  mergeConfidence: 0,
});

function table(overrides = {}) {
  return {
    id: "page-0-table-0",
    bounds: { x: 0.1, y: 0.1, width: 0.8, height: 0.4 },
    evidence: "mixed",
    confidence: 0.9,
    columns: [{ x0: 0.1, x1: 0.5 }, { x0: 0.5, x1: 0.9 }],
    rows: [row(0.1, 0.3, "header"), row(0.3, 0.5)],
    header: { rowCount: 1, labels: ["Item", "Amount"] },
    rulings: { vertical: [], horizontal: [] },
    ...overrides,
  };
}

function structure(tables) {
  return { version: 1, coordinateSpace: "normalized", pages: [{ pageIndex: 0, tables }] };
}

// A well-formed model survives untouched.
{
  const parsed = parseTableStructure(structure([table()]));
  assert.equal(parsed.pages[0].tables.length, 1);
  assert.equal(parsed.pages[0].tables[0].header.rowCount, 1);
}

// A payload that is not a table model at all is rejected outright.
for (const bad of [null, 42, {}, { version: 2, coordinateSpace: "normalized", pages: [] },
  { version: 1, coordinateSpace: "absolute", pages: [] }, { version: 1, coordinateSpace: "normalized" }]) {
  assert.throws(() => parseTableStructure(bad), /table structure/i);
}

// Individual malformed tables are dropped; the page still loads.
const malformed = [
  table({ bounds: { x: 0.1, y: 0.1, width: -0.2, height: 0.4 } }),
  table({ bounds: { x: 0.9, y: 0.1, width: 0.4, height: 0.4 } }),
  table({ columns: [{ x0: 0.5, x1: 0.2 }, { x0: 0.2, x1: 0.9 }] }),
  table({ columns: [{ x0: 0.1, x1: 0.5 }] }),
  table({ rows: [row(0.4, 0.5), row(0.1, 0.3)] }),
  table({ rows: [row(0.1, 0.3)] }),
  table({ evidence: "guessed" }),
  table({ confidence: 4 }),
  table({ id: "" }),
  table({ header: { rowCount: 9, labels: [] } }),
  table({ header: { rowCount: 1, labels: [7] } }),
  table({ rows: [{ ...row(0.1, 0.3), kind: "footer" }, row(0.3, 0.5)] }),
  table({ rows: [{ ...row(0.1, 0.3), merged: "yes" }, row(0.3, 0.5)] }),
];
for (const entry of malformed) {
  const parsed = parseTableStructure(structure([entry]));
  assert.deepEqual(parsed.pages[0].tables, [], JSON.stringify(entry).slice(0, 90));
}

// One bad table does not take its good neighbour with it.
{
  const parsed = parseTableStructure(structure([table({ evidence: "guessed" }), table({ id: "good" })]));
  assert.equal(parsed.pages[0].tables.length, 1);
  assert.equal(parsed.pages[0].tables[0].id, "good");
}

// The period a table covers rides alongside the header.
{
  const period = { table: "2025", columns: ["", "2025"], qualifier: "Years ended" };
  const parsed = parseTableStructure(structure([table({ period })]));
  assert.deepEqual(parsed.pages[0].tables[0].period, period);
}

// Absent is normal: most tables carry no period at all.
{
  const parsed = parseTableStructure(structure([table()]));
  assert.equal(parsed.pages[0].tables[0].period, null);
  const explicit = parseTableStructure(structure([table({ period: null })]));
  assert.equal(explicit.pages[0].tables[0].period, null);
}

// A period a caller cannot index alongside the columns is not usable.
for (const period of [
  { table: "2025", columns: [""], qualifier: "" },                  // one entry short
  { table: "2025", columns: ["", "2025", "2024"], qualifier: "" },  // one too many
  { table: 2025, columns: ["", ""], qualifier: "" },
  { table: "", columns: "2025", qualifier: "" },
  { table: "", columns: ["", 2024], qualifier: "" },
  { table: "", columns: ["", ""] },
]) {
  const parsed = parseTableStructure(structure([table({ period })]));
  assert.deepEqual(parsed.pages[0].tables, [], JSON.stringify(period));
}

// A missing header is normal, and unusable rulings degrade to empty lists.
{
  const parsed = parseTableStructure(structure([table({ header: null, rulings: "nonsense" })]));
  assert.equal(parsed.pages[0].tables[0].header, null);
  assert.deepEqual(parsed.pages[0].tables[0].rulings, { vertical: [], horizontal: [] });
}

// Pages that are not pages are skipped rather than throwing.
{
  const parsed = parseTableStructure({
    version: 1,
    coordinateSpace: "normalized",
    pages: [null, { pageIndex: -1, tables: [] }, { pageIndex: 3, tables: "no" }, { pageIndex: 4, tables: [] }],
  });
  assert.deepEqual(parsed.pages.map((page) => page.pageIndex), [4]);
}

console.log("table structure validation tests passed");
