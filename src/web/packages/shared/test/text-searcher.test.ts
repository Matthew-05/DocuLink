import assert from "node:assert/strict";
import { registerHooks } from "node:module";

const textSearcherUrl = new URL("../src/text-searcher.ts", import.meta.url).href;
const zeroPlaceholderUrl = new URL("../src/zero-placeholder.ts", import.meta.url).href;

registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier === "./zero-placeholder.js" && context.parentURL === textSearcherUrl) {
      return { url: zeroPlaceholderUrl, shortCircuit: true };
    }
    return nextResolve(specifier, context);
  },
});

const textSearcher = await import(textSearcherUrl) as typeof import("../src/text-searcher.ts");
const {
  buildSearchPageIndexFromEntries,
  cleanAutoInsertedSearchQuery,
  normalizeLinkerQuery,
  normalizeSearchQuery,
  searchPage,
  searchPageWithIndex,
} = textSearcher;

function entriesFromText(text: string, lineBreaks: ReadonlySet<number> = new Set<number>()) {
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
assert.equal(normalizeSearchQuery("Invoice..."), "invoice");
assert.equal(normalizeLinkerQuery(" 03/05/2026 "), "3/5/2026");
assert.equal(normalizeLinkerQuery("2026-03-05"), "3/5/2026");
assert.equal(normalizeLinkerQuery("March 5th, 2026"), "3/5/2026");
assert.equal(normalizeLinkerQuery("5 Mar 2026"), "3/5/2026");
assert.equal(normalizeLinkerQuery("03/05/26"), "3/5/2026");
assert.equal(normalizeLinkerQuery("03/05/30"), "3/5/2030");
assert.equal(normalizeLinkerQuery("02/30/2026"), "02/30/2026");
assert.equal(normalizeLinkerQuery("Acme Corp,"), "acme corp");

assert.equal(cleanAutoInsertedSearchQuery("       $1,234.56   "), "1,234.56");
assert.equal(cleanAutoInsertedSearchQuery(" \u20ac 1.234,56 "), "1.234,56");
assert.equal(cleanAutoInsertedSearchQuery("\t Invoice\r\n1042 \u00a0"), "Invoice 1042");
assert.equal(cleanAutoInsertedSearchQuery("Acme\u200b\u2060 Corp"), "Acme Corp");
assert.equal(cleanAutoInsertedSearchQuery("PO\u0000123"), "PO 123");
assert.equal(cleanAutoInsertedSearchQuery("  March   5, 2026  "), "March 5, 2026");
assert.equal(cleanAutoInsertedSearchQuery(" Invoice?! "), "Invoice");
assert.equal(cleanAutoInsertedSearchQuery(" (1,000) "), "(1,000)");
assert.equal(cleanAutoInsertedSearchQuery(" ($1,234.56) "), "(1,234.56)");
assert.equal(normalizeSearchQuery(cleanAutoInsertedSearchQuery(" ($1,234.56) ")), "-1234.56");
assert.equal(cleanAutoInsertedSearchQuery("Invoice)"), "Invoice");

{
  const entries = entriesFromText("total 1,000 due");
  const matches = searchPage("pdf-1", "Invoice", 0, entries, normalizeSearchQuery("1000"));
  assert.equal(matches.length, 1);
  assert.equal(matches[0]!.id, "pdf-1:0:6");
  assert.equal(matches[0]!.contextText, "1,000");
}

{
  const text = "assets — liabilities";
  const entries = entriesFromText(text);
  const matches = searchPage("pdf-1", "Balance Sheet", 0, entries, normalizeSearchQuery("0"));
  const dashIndex = text.indexOf("—");

  assert.equal(matches.length, 1, "a standalone em dash should match a zero search");
  assert.equal(matches[0]!.id, `pdf-1:0:${dashIndex}`);
  assert.equal(matches[0]!.exactMatch, true);
  assert.equal(matches[0]!.contextText, "—");
  assert.deepEqual(matches[0]!.matchInContext, { start: 0, end: 1 });
  assert.ok(Math.abs(matches[0]!.highlightRect.x - dashIndex / text.length) < Number.EPSILON);
  assert.ok(Math.abs(matches[0]!.highlightRect.width - 1 / text.length) < Number.EPSILON);
  assert.equal(matches[0]!.highlightRect.y, 0);
  assert.equal(matches[0]!.highlightRect.height, 0.05);
}

{
  const entries = entriesFromText("cash $ —");
  const matches = searchPage("pdf-1", "Balance Sheet", 0, entries, normalizeSearchQuery("0"));

  assert.equal(matches.length, 1, "a currency-prefixed standalone em dash should match zero");
  assert.equal(matches[0]!.contextText, "—");
}

{
  const entries = entriesFromText("well—being");
  const matches = searchPage("pdf-1", "Notes", 0, entries, normalizeSearchQuery("0"));

  assert.equal(matches.length, 0, "an em dash inside a word should not be treated as zero");
}

{
  const entries = entriesFromText("A—B");
  entries[0]!.normLeft = 0.05;
  entries[0]!.normRight = 0.10;
  entries[1]!.normLeft = 0.40;
  entries[1]!.normRight = 0.45;
  entries[2]!.normLeft = 0.75;
  entries[2]!.normRight = 0.80;

  const matches = searchPage("pdf-1", "Balance Sheet", 0, entries, normalizeSearchQuery("0"));
  assert.equal(matches.length, 1, "a visually isolated em dash should match zero without text spaces");
  assert.equal(matches[0]!.contextText, "—");
}

{
  const entries = entriesFromText("$—B");
  entries[0]!.normLeft = 0.05;
  entries[0]!.normRight = 0.10;
  entries[1]!.normLeft = 0.40;
  entries[1]!.normRight = 0.45;
  entries[2]!.normLeft = 0.75;
  entries[2]!.normRight = 0.80;

  const matches = searchPage("pdf-1", "Balance Sheet", 0, entries, normalizeSearchQuery("0"));
  assert.equal(matches.length, 1, "a visually isolated currency dash should match zero without text spaces");
  assert.equal(matches[0]!.contextText, "—");
}

{
  const entries = entriesFromText("variance (1,000) due");
  const matches = searchPage("pdf-1", "Invoice", 0, entries, normalizeSearchQuery("-1000"));
  assert.equal(matches.length, 1);
  assert.equal(matches[0]!.id, "pdf-1:0:9");
  assert.equal(matches[0]!.contextText, "(1,000)");
  assert.deepEqual(matches[0]!.matchInContext, { start: 0, end: 7 });
}

{
  const entries = entriesFromText("variance (11) due");
  const matches = searchPage("pdf-1", "Invoice", 0, entries, normalizeSearchQuery("11"));
  assert.equal(matches.length, 1);
  assert.equal(matches[0]!.contextText, "(11)");
  assert.deepEqual(matches[0]!.matchInContext, { start: 1, end: 3 });
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
    normalizeLinkerQuery("3/5/2026"),
  );
  assert.equal(matches.length, 1, `expected formatted date to match ${sourceDate}`);
  assert.equal(matches[0]!.exactMatch, true);
  assert.equal(matches[0]!.contextText, sourceDate);
  assert.deepEqual(matches[0]!.matchInContext, { start: 0, end: sourceDate.length });
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
    normalizeLinkerQuery("12/31/2025"),
  );
  assert.equal(matches.length, 1, "linker index should find an exact formatted date");
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
    normalizeLinkerQuery("8/7/2026"),
  );
  assert.equal(matches.length, 1, "linker should respect a PDF line boundary before a date");
  assert.equal(matches[0]!.contextText, "08/07/26");
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
  const entries = entriesFromText("111 11.00 11,");
  const matches = searchPageWithIndex(
    "pdf-1",
    "Numbers",
    0,
    entries,
    buildSearchPageIndexFromEntries(entries),
    normalizeSearchQuery("11"),
    { limit: 1 },
  );
  assert.deepEqual(matches.map((match) => match.contextText), ["11"]);
}

{
  const entries = entriesFromText("111 11.00 A11 11, 11");
  const matches = searchPage("pdf-1", "Numbers", 0, entries, normalizeSearchQuery("11"));
  assert.deepEqual(
    matches.map((match) => [match.id, match.exactMatch, match.contextText]),
    [
      ["pdf-1:0:14", true, "11"],
      ["pdf-1:0:18", true, "11"],
      ["pdf-1:0:0", false, "111"],
      ["pdf-1:0:1", false, "111"],
      ["pdf-1:0:4", false, "11.00"],
      ["pdf-1:0:11", false, "A11"],
    ],
  );
}

{
  const entries = entriesFromText("ab", new Set([1]));
  const matches = searchPage("pdf-1", "Invoice", 0, entries, "ab");
  assert.equal(matches.length, 0);
}

console.log("[Talliark] text-searcher tests passed");
