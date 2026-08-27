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
  cleanAutoInsertedSearchQuery,
  normalizeMatcherQuery,
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
assert.equal(normalizeSearchQuery("08/07/26"), "08/07/26");
assert.equal(normalizeMatcherQuery(" 03/05/2026 "), "3/5/2026");
assert.equal(normalizeMatcherQuery("2026-03-05"), "3/5/2026");
assert.equal(normalizeMatcherQuery("March 5th, 2026"), "3/5/2026");
assert.equal(normalizeMatcherQuery("5 Mar 2026"), "3/5/2026");
assert.equal(normalizeMatcherQuery("03/05/26"), "3/5/2026");
assert.equal(normalizeMatcherQuery("03/05/30"), "3/5/2030");
assert.equal(normalizeMatcherQuery("02/30/2026"), "02/30/2026");

assert.equal(cleanAutoInsertedSearchQuery("       $1,234.56   "), "1,234.56");
assert.equal(cleanAutoInsertedSearchQuery(" \u20ac 1.234,56 "), "1.234,56");
assert.equal(cleanAutoInsertedSearchQuery("\t Invoice\r\n1042 \u00a0"), "Invoice 1042");
assert.equal(cleanAutoInsertedSearchQuery("Acme\u200b\u2060 Corp"), "Acme Corp");
assert.equal(cleanAutoInsertedSearchQuery("PO\u0000123"), "PO 123");
assert.equal(cleanAutoInsertedSearchQuery("  March   5, 2026  "), "March 5, 2026");

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

for (const sourceDate of ["03/05/2026", "2026-03-05", "March 5th, 2026", "5 Mar 2026", "03/05/26"]) {
  const entries = entriesFromText(`dated ${sourceDate} due`);
  const index = buildSearchPageIndexFromEntries(entries, { normalizeDates: true });
  const matches = searchPageWithIndex(
    "pdf-1",
    "Invoice",
    0,
    entries,
    index,
    normalizeMatcherQuery("3/5/2026"),
  );
  assert.equal(matches.length, 1, `expected formatted date to match ${sourceDate}`);
  assert.equal(matches[0].contextText, sourceDate);
  assert.deepEqual(matches[0].matchInContext, { start: 0, end: sourceDate.length });
}

{
  const entries = entriesFromText("dated 12/31/2025 due");
  const index = buildSearchPageIndexFromEntries(entries, { normalizeDates: true });
  const matches = searchPageWithIndex(
    "pdf-1",
    "Invoice",
    0,
    entries,
    index,
    normalizeMatcherQuery("12/31/2025"),
  );
  assert.equal(matches.length, 1, "matcher index should find an exact formatted date");
}

{
  const entries = entriesFromText("Door Works08/07/26 A/R Aging Detail", new Set([10]));
  const index = buildSearchPageIndexFromEntries(entries, { normalizeDates: true });
  const matches = searchPageWithIndex(
    "pdf-1",
    "A/R Aging Detail",
    0,
    entries,
    index,
    normalizeMatcherQuery("8/7/2026"),
  );
  assert.equal(matches.length, 1, "matcher should respect a PDF line boundary before a date");
  assert.equal(matches[0].contextText, "08/07/26");
}

{
  const entries = entriesFromText("dated 08/07/26 due");
  const matches = searchPage("pdf-1", "Invoice", 0, entries, normalizeSearchQuery("8/7/2026"));
  assert.equal(matches.length, 0, "ordinary search should not expand date equivalents");
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
