import assert from "node:assert/strict";
import test from "node:test";
import { detectTableGrid } from "../src/services/table-extractor.ts";

interface TestCharacter {
  char: string;
  normLeft: number;
  normTop: number;
  normRight: number;
  normBottom: number;
  lineIndex: number;
  itemIndex: number;
  spacesPrecomputed: boolean;
}

let itemIndex = 0;

function text(
  value: string,
  left: number,
  top: number,
  lineIndex: number,
  charWidth = 0.015,
): TestCharacter[] {
  return [...value].map((char, index) => ({
    char,
    normLeft: left + index * charWidth,
    normTop: top,
    normRight: left + (index + 1) * charWidth,
    normBottom: top + 0.025,
    lineIndex,
    itemIndex: itemIndex++,
    spacesPrecomputed: true,
  }));
}

const rect = { x: 0, y: 0, width: 1, height: 1 };

test("ignores whitespace-only source lines and anchors the first visible row at cells[0]", () => {
  const entries = [
    ...text("   ", 0.1, 0.05, 0),
    ...text("Header", 0.1, 0.2, 1),
    ...text("100", 0.65, 0.2, 1),
    ...text("Detail", 0.1, 0.4, 2),
    ...text("200", 0.65, 0.4, 2),
  ];

  const grid = detectTableGrid(entries, rect);

  assert.equal(grid.rowBoundaries.length, 1);
  assert.deepEqual(grid.cells, [["Header", "100"], ["Detail", "200"]]);
});

test("merges source line IDs that occupy the same visual row", () => {
  const entries = [
    ...text("Alpha", 0.1, 0.2, 1),
    ...text("10", 0.65, 0.201, 2),
    ...text("Beta", 0.1, 0.4, 3),
    ...text("20", 0.65, 0.4, 3),
  ];

  const grid = detectTableGrid(entries, rect);

  assert.equal(grid.rowBoundaries.length, 1);
  assert.deepEqual(grid.cells, [["Alpha", "10"], ["Beta", "20"]]);
});

test("finds shared column gutters when labels and values have uneven widths", () => {
  const entries = [
    ...text("Long description", 0.08, 0.15, 1),
    ...text("Q1", 0.48, 0.15, 1),
    ...text("100", 0.78, 0.15, 1),
    ...text("Short", 0.08, 0.35, 2),
    ...text("Quarter", 0.48, 0.35, 2),
    ...text("200", 0.78, 0.35, 2),
    ...text("Medium label", 0.08, 0.55, 3),
    ...text("Q3", 0.48, 0.55, 3),
    ...text("300", 0.78, 0.55, 3),
  ];

  const grid = detectTableGrid(entries, rect);

  assert.equal(grid.columnBoundaries.length, 2);
  assert.deepEqual(grid.cells, [
    ["Long description", "Q1", "100"],
    ["Short", "Quarter", "200"],
    ["Medium label", "Q3", "300"],
  ]);
});
