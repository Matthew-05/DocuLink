import assert from "node:assert/strict";
import { buildCharEntriesFromGeometry } from "../src/char-entries.ts";
import type { TextGeometry } from "../src/geometry-decoder.ts";

const geometry: TextGeometry = {
  version: 1,
  coordinateSpace: "normalized",
  pages: [
    {
      pageIndex: 0,
      characters: [
        { char: "7", x: 0.1, y: 0.1, width: 0.02, height: 0.04, lineIndex: 0 },
        { char: "n", x: 0.1, y: 0.115, width: 0.02, height: 0.025, lineIndex: 1 },
      ],
    },
  ],
};

const entries = buildCharEntriesFromGeometry(geometry).get(0);
assert.ok(entries);
assert.deepEqual(entries.map((entry) => entry.lineIndex), [0, 1]);

const duplicateLayerGeometry: TextGeometry = {
  version: 1,
  coordinateSpace: "normalized",
  pages: [
    {
      pageIndex: 0,
      characters: [
        { char: "A", x: 0.1, y: 0.1, width: 0.02, height: 0.04, lineIndex: 0 },
        { char: "1", x: 0.12, y: 0.1, width: 0.02, height: 0.04, lineIndex: 0 },
        { char: "A", x: 0.1002, y: 0.1001, width: 0.02, height: 0.04, lineIndex: 1 },
        { char: "1", x: 0.1202, y: 0.1001, width: 0.02, height: 0.04, lineIndex: 1 },
        { char: "A", x: 0.1, y: 0.2, width: 0.02, height: 0.04, lineIndex: 2 },
        { char: "1", x: 0.12, y: 0.2, width: 0.02, height: 0.04, lineIndex: 2 },
      ],
    },
  ],
};

const deduplicatedEntries = buildCharEntriesFromGeometry(duplicateLayerGeometry).get(0);
assert.ok(deduplicatedEntries);
assert.equal(deduplicatedEntries.map((entry) => entry.char).join(""), "A1A1");
assert.deepEqual(deduplicatedEntries.map((entry) => entry.lineIndex), [0, 0, 1, 1]);

console.log("[Talliark] geometry line tests passed");
