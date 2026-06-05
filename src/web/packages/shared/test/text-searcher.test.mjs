import assert from "node:assert/strict";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import ts from "typescript";

const packageRoot = dirname(dirname(fileURLToPath(import.meta.url)));
const outdir = join(packageRoot, "test", ".tmp");
const outfile = join(outdir, "text-searcher.mjs");
const sourceFile = join(packageRoot, "src", "text-searcher.ts");

await rm(outdir, { recursive: true, force: true });
await mkdir(outdir, { recursive: true });

const source = await readFile(sourceFile, "utf8");
const transpiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ESNext,
    target: ts.ScriptTarget.ES2022,
    verbatimModuleSyntax: true,
  },
});
await writeFile(outfile, transpiled.outputText, "utf8");

const {
  buildSearchPageIndexFromEntries,
  normalizeSearchQuery,
  searchPage,
  searchPageWithIndex,
} = await import(pathToFileURL(outfile).href);

function entriesFromText(text, lineBreaks = new Set()) {
  let lineIndex = 0;
  return Array.from(text, (char, i) => {
    if (lineBreaks.has(i)) lineIndex++;
    return {
      char,
      lineIndex,
      itemIndex: i,
      normLeft: i / text.length,
      normTop: lineIndex * 0.1,
      normRight: (i + 1) / text.length,
      normBottom: lineIndex * 0.1 + 0.05,
    };
  });
}

assert.equal(normalizeSearchQuery(" 1,000 "), "1000");
assert.equal(normalizeSearchQuery(" (1,000) "), "-1000");

{
  const entries = entriesFromText("total 1,000 due");
  const matches = searchPage("pdf-1", "Invoice", 0, entries, normalizeSearchQuery("1000"));
  assert.equal(matches.length, 1);
  assert.equal(matches[0].id, "pdf-1:0:6");
  assert.equal(matches[0].contextText, "1,000");
}

{
  const entries = entriesFromText("variance (1,000) due");
  const matches = searchPage("pdf-1", "Invoice", 0, entries, normalizeSearchQuery("-1000"));
  assert.equal(matches.length, 1);
  assert.equal(matches[0].id, "pdf-1:0:9");
  assert.equal(matches[0].contextText, "(1,000)");
  assert.deepEqual(matches[0].matchInContext, { start: 0, end: 7 });
}

{
  const entries = entriesFromText("fee fee fee");
  const index = buildSearchPageIndexFromEntries(entries);
  const matches = searchPageWithIndex("pdf-1", "Invoice", 0, entries, index, "fee", { limit: 2 });
  assert.equal(matches.length, 2);
  assert.deepEqual(matches.map((m) => m.id), ["pdf-1:0:0", "pdf-1:0:4"]);
}

{
  const entries = entriesFromText("ab", new Set([1]));
  const matches = searchPage("pdf-1", "Invoice", 0, entries, "ab");
  assert.equal(matches.length, 0);
}

console.log("[DocuLink] text-searcher tests passed");
await rm(outdir, { recursive: true, force: true });
