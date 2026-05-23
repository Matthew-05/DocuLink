import type { TextContentCache, CharacterEntry } from "../../services/text-content-cache.js";
import type { NormalizedRect, PdfEntry, SearchMatch } from "../../types/index.js";

/** Normalizes a user query before searching. Extensible hook for future rules. */
export function normalizeSearchQuery(raw: string): string {
  return raw.trim().toLowerCase();
}

/** Returns true if `pageText` contains `normalizedQuery`. Extensible hook. */
export function pageTextMatchesQuery(pageText: string, normalizedQuery: string): boolean {
  return pageText.toLowerCase().includes(normalizedQuery);
}

export class PdfTextSearcher {
  constructor(private readonly _cache: TextContentCache) {}

  search(rawQuery: string, pdfEntries: PdfEntry[]): SearchMatch[] {
    const normalizedQuery = normalizeSearchQuery(rawQuery);
    if (!normalizedQuery) return [];

    const results: SearchMatch[] = [];

    for (const entry of pdfEntries) {
      if (!this._cache.has(entry.id)) continue;

      for (const pageIndex of this._cache.getPageIndices(entry.id)) {
        const entries = this._cache.get(entry.id, pageIndex);
        if (!entries || entries.length === 0) continue;

        results.push(
          ...this._searchPage(entry.id, entry.name, pageIndex, entries, normalizedQuery),
        );
      }
    }

    return results;
  }

  private _searchPage(
    pdfId: string,
    pdfName: string,
    pageIndex: number,
    entries: CharacterEntry[],
    normalizedQuery: string,
  ): SearchMatch[] {
    const pageText = entries.map((e) => e.char).join("");
    if (!pageTextMatchesQuery(pageText, normalizedQuery)) return [];
    const normalizedPageText = pageText.toLowerCase();
    const queryLen = normalizedQuery.length;
    const matches: SearchMatch[] = [];

    let offset = 0;
    while (offset <= normalizedPageText.length - queryLen) {
      const hitIndex = normalizedPageText.indexOf(normalizedQuery, offset);
      if (hitIndex === -1) break;

      const matchEnd = hitIndex + queryLen;
      const { contextText, matchInContext } = expandToWord(pageText, hitIndex, matchEnd);
      const highlightRect = rectFromEntries(entries, hitIndex, matchEnd - 1);

      matches.push({
        id: `${pdfId}:${pageIndex}:${hitIndex}`,
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
}

function expandToWord(
  text: string,
  matchStart: number,
  matchEnd: number,
): { contextText: string; matchInContext: { start: number; end: number } } {
  let wordStart = matchStart;
  while (wordStart > 0 && !/\s/.test(text[wordStart - 1] ?? "")) {
    wordStart--;
  }

  let wordEnd = matchEnd;
  while (wordEnd < text.length && !/\s/.test(text[wordEnd] ?? "")) {
    wordEnd++;
  }

  const contextText = text.slice(wordStart, wordEnd);
  return {
    contextText,
    matchInContext: {
      start: matchStart - wordStart,
      end: matchEnd - wordStart,
    },
  };
}

function rectFromEntries(
  entries: CharacterEntry[],
  startIndex: number,
  endIndex: number,
): NormalizedRect {
  const slice = entries.slice(startIndex, endIndex + 1);
  if (slice.length === 0) {
    return { x: 0, y: 0, width: 0, height: 0 };
  }

  const normLeft   = Math.min(...slice.map((e) => e.normLeft));
  const normTop    = Math.min(...slice.map((e) => e.normTop));
  const normRight  = Math.max(...slice.map((e) => e.normRight));
  const normBottom = Math.max(...slice.map((e) => e.normBottom));

  return {
    x: normLeft,
    y: normTop,
    width: normRight - normLeft,
    height: normBottom - normTop,
  };
}
