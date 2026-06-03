import type { CharacterEntry } from "./char-entries.js";
import type { NormalizedRect, SearchMatch } from "./types.js";

function isNumericComma(text: string, i: number): boolean {
  return text[i] === "," && /\d/.test(text[i - 1] ?? "") && /\d/.test(text[i + 1] ?? "");
}

export function normalizeSearchQuery(raw: string): string {
  const lower = raw.trim().toLowerCase();
  let result = "";
  for (let i = 0; i < lower.length; i++) {
    if (!isNumericComma(lower, i)) result += lower[i];
  }
  return result;
}

export function pageTextMatchesQuery(pageText: string, normalizedQuery: string): boolean {
  const lower = pageText.toLowerCase();
  let stripped = "";
  for (let i = 0; i < lower.length; i++) {
    if (!isNumericComma(lower, i)) stripped += lower[i];
  }
  return stripped.includes(normalizedQuery);
}

function buildNormalizedPageText(text: string): { normalized: string; indexMap: number[] } {
  const indexMap: number[] = [];
  let normalized = "";
  for (let i = 0; i < text.length; i++) {
    if (!isNumericComma(text, i)) {
      indexMap.push(i);
      normalized += text[i];
    }
  }
  return { normalized, indexMap };
}

function matchSpansSingleLine(entries: CharacterEntry[], start: number, end: number): boolean {
  for (let i = start; i < end - 1; i++) {
    const curr = entries[i];
    const next = entries[i + 1];
    if (curr === undefined || next === undefined || curr.lineIndex !== next.lineIndex) {
      return false;
    }
  }
  return true;
}

function expandToWord(
  text: string,
  entries: CharacterEntry[],
  matchStart: number,
  matchEnd: number,
): { contextText: string; matchInContext: { start: number; end: number } } {
  let wordStart = matchStart;
  while (
    wordStart > 0
    && !/\s/.test(text[wordStart - 1] ?? "")
    && entries[wordStart]?.lineIndex === entries[wordStart - 1]?.lineIndex
  ) {
    wordStart--;
  }

  let wordEnd = matchEnd;
  while (
    wordEnd < text.length
    && !/\s/.test(text[wordEnd] ?? "")
    && entries[wordEnd - 1]?.lineIndex === entries[wordEnd]?.lineIndex
  ) {
    wordEnd++;
  }

  return {
    contextText: text.slice(wordStart, wordEnd),
    matchInContext: { start: matchStart - wordStart, end: matchEnd - wordStart },
  };
}

function rectFromEntries(
  entries: CharacterEntry[],
  startIndex: number,
  endIndex: number,
): NormalizedRect {
  const slice = entries.slice(startIndex, endIndex + 1);
  if (slice.length === 0) return { x: 0, y: 0, width: 0, height: 0 };

  const normLeft   = Math.min(...slice.map((e) => e.normLeft));
  const normTop    = Math.min(...slice.map((e) => e.normTop));
  const normRight  = Math.max(...slice.map((e) => e.normRight));
  const normBottom = Math.max(...slice.map((e) => e.normBottom));

  return { x: normLeft, y: normTop, width: normRight - normLeft, height: normBottom - normTop };
}

export function searchPage(
  pdfId: string,
  pdfName: string,
  pageIndex: number,
  entries: CharacterEntry[],
  normalizedQuery: string,
): SearchMatch[] {
  if (!normalizedQuery || entries.length === 0) return [];

  const pageText = entries.map((e) => e.char).join("");
  if (!pageTextMatchesQuery(pageText, normalizedQuery)) return [];

  const { normalized: normalizedPageText, indexMap } = buildNormalizedPageText(pageText.toLowerCase());
  const queryLen = normalizedQuery.length;
  const matches: SearchMatch[] = [];

  let offset = 0;
  while (offset <= normalizedPageText.length - queryLen) {
    const hitIndex = normalizedPageText.indexOf(normalizedQuery, offset);
    if (hitIndex === -1) break;

    const originalStart = indexMap[hitIndex]!;
    const originalEnd = indexMap[hitIndex + queryLen - 1]! + 1;

    if (!matchSpansSingleLine(entries, originalStart, originalEnd)) {
      offset = hitIndex + 1;
      continue;
    }

    const { contextText, matchInContext } = expandToWord(pageText, entries, originalStart, originalEnd);
    const highlightRect = rectFromEntries(entries, originalStart, originalEnd - 1);

    matches.push({
      id: `${pdfId}:${pageIndex}:${originalStart}`,
      pdfId,
      pdfName,
      pageIndex,
      contextText,
      matchInContext,
      highlightRect,
    });

    offset = hitIndex + 1;
  }

  return matches;
}
