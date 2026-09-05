import assert from "node:assert/strict";
import { registerHooks } from "node:module";
import test from "node:test";

const textExtractorUrl = new URL("../src/text-extractor.ts", import.meta.url).href;
const zeroPlaceholderUrl = new URL("../src/zero-placeholder.ts", import.meta.url).href;

registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier === "./zero-placeholder.js" && context.parentURL === textExtractorUrl) {
      return { url: zeroPlaceholderUrl, shortCircuit: true };
    }
    return nextResolve(specifier, context);
  },
});

const { extractText } = await import(textExtractorUrl) as typeof import("../src/text-extractor.ts");

function entriesFromText(text: string) {
  return Array.from(text, (char, index) => ({
    char,
    lineIndex: 0,
    itemIndex: index,
    normLeft: index / text.length,
    normTop: 0,
    normRight: (index + 1) / text.length,
    normBottom: 0.05,
    spacesPrecomputed: true,
  }));
}

function entry(
  char: string,
  lineIndex: number,
  itemIndex: number,
  left: number,
  top: number,
  right: number,
  bottom: number,
) {
  return {
    char,
    lineIndex,
    itemIndex,
    normLeft: left,
    normTop: top,
    normRight: right,
    normBottom: bottom,
    spacesPrecomputed: true,
  };
}

const fullRect = { x: 0, y: 0, width: 1, height: 1 };

test("normalizes complete financial zero placeholders without changing other text", () => {
  assert.equal(extractText(entriesFromText("—"), fullRect), "0");
  assert.equal(extractText(entriesFromText("$ —"), fullRect), "$ 0");
  assert.equal(extractText(entriesFromText("$ 1"), fullRect), "$ 1");
  assert.equal(
    extractText(entriesFromText("Revenue — expense"), fullRect),
    "Revenue — expense",
  );
});

test("orders raised copyright and trademark signs by horizontal position", () => {
  for (const symbol of ["©", "®", "™"]) {
    const entries = [
      entry(symbol, 0, 0, 0.30, 0.01, 0.35, 0.05),
      entry("4", 1, 1, 0.10, 0.03, 0.20, 0.10),
      entry("R", 1, 2, 0.20, 0.03, 0.30, 0.10),
    ];

    assert.equal(extractText(entries, fullRect), `4R${symbol}`);
  }
});

test("keeps superscripts and subscripts inline with their base text", () => {
  const superscript = [
    entry("2", 0, 0, 0.20, 0.01, 0.24, 0.05),
    entry("x", 1, 1, 0.10, 0.03, 0.20, 0.10),
  ];
  const subscript = [
    entry("2", 0, 0, 0.20, 0.07, 0.24, 0.11),
    entry("H", 1, 1, 0.10, 0.03, 0.20, 0.10),
    entry("O", 1, 2, 0.24, 0.03, 0.34, 0.10),
  ];

  assert.equal(extractText(superscript, fullRect), "x2");
  assert.equal(extractText(subscript, fullRect), "H2O");
});

test("does not fold a separate small line into the line below it", () => {
  const entries = [
    entry("1", 0, 0, 0.10, 0.01, 0.14, 0.05),
    entry("A", 1, 1, 0.10, 0.08, 0.20, 0.15),
  ];

  assert.equal(extractText(entries, fullRect), "1 A");
});
