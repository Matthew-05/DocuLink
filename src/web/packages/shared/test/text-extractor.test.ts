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

const { extractText } = await import(textExtractorUrl);

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
