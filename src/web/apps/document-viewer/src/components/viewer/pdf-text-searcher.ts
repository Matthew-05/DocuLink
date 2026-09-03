import { normalizeSearchQuery, searchPageWithIndex } from "@doculink/shared";
import type { TextContentCache } from "../../services/text-content-cache.js";
import type { PdfEntry, SearchMatch } from "../../types/index.js";

export { normalizeSearchQuery } from "@doculink/shared";

interface SearchPageRef {
  entry: PdfEntry;
  pageIndex: number;
}

export interface SearchBatch {
  matches: SearchMatch[];
  complete: boolean;
  hasMore: boolean;
}

export class PdfTextSearcher {
  private readonly _cache: TextContentCache;

  constructor(cache: TextContentCache) {
    this._cache = cache;
  }

  search(rawQuery: string, pdfEntries: PdfEntry[]): SearchMatch[] {
    const normalizedQuery = normalizeSearchQuery(rawQuery);
    if (!normalizedQuery) return [];

    const results: SearchMatch[] = [];

    for (const entry of pdfEntries) {
      if (!this._cache.has(entry.id)) continue;

      for (const pageIndex of this._cache.getPageIndices(entry.id)) {
        const entries = this._cache.get(entry.id, pageIndex);
        const searchIndex = this._cache.getSearchIndex(entry.id, pageIndex);
        if (!entries || entries.length === 0 || !searchIndex) continue;

        results.push(...searchPageWithIndex(entry.id, entry.name, pageIndex, entries, searchIndex, normalizedQuery));
      }
    }

    return results.sort((a, b) => Number(b.exactMatch) - Number(a.exactMatch));
  }

  createSession(rawQuery: string, pdfEntries: PdfEntry[]): PdfTextSearchSession {
    return new PdfTextSearchSession(this._cache, normalizeSearchQuery(rawQuery), pdfEntries);
  }

  searchPage(rawQuery: string, entry: PdfEntry, pageIndex: number): SearchMatch[] {
    const normalizedQuery = normalizeSearchQuery(rawQuery);
    if (!normalizedQuery || !this._cache.has(entry.id)) return [];

    const entries = this._cache.get(entry.id, pageIndex);
    const searchIndex = this._cache.getSearchIndex(entry.id, pageIndex);
    if (!entries || entries.length === 0 || !searchIndex) return [];

    return searchPageWithIndex(entry.id, entry.name, pageIndex, entries, searchIndex, normalizedQuery);
  }
}

export class PdfTextSearchSession {
  private readonly _cache: TextContentCache;
  private readonly _normalizedQuery: string;
  private readonly _pages: SearchPageRef[];
  private _pageCursor = 0;
  private _matchCursor = 0;
  private _complete = false;
  private _priority: "exact" | "partial" = "exact";

  constructor(
    cache: TextContentCache,
    normalizedQuery: string,
    pdfEntries: PdfEntry[],
  ) {
    this._cache = cache;
    this._normalizedQuery = normalizedQuery;
    this._pages = [];

    if (!this._normalizedQuery) {
      this._complete = true;
      return;
    }

    for (const entry of pdfEntries) {
      if (!this._cache.has(entry.id)) continue;

      for (const pageIndex of this._cache.getPageIndices(entry.id)) {
        this._pages.push({ entry, pageIndex });
      }
    }
  }

  nextBatch(limit: number, pageBudget = 12): SearchBatch {
    if (this._complete || limit <= 0) {
      return { matches: [], complete: this._complete, hasMore: !this._complete };
    }

    const matches: SearchMatch[] = [];
    let pagesScanned = 0;

    while (matches.length < limit && pagesScanned < pageBudget && !this._complete) {
      if (this._pageCursor >= this._pages.length) {
        if (this._priority === "exact") {
          this._priority = "partial";
          this._pageCursor = 0;
          this._matchCursor = 0;
          continue;
        }

        this._complete = true;
        break;
      }

      const page = this._pages[this._pageCursor]!;
      const entries = this._cache.get(page.entry.id, page.pageIndex);
      const searchIndex = this._cache.getSearchIndex(page.entry.id, page.pageIndex);

      if (!entries || entries.length === 0 || !searchIndex) {
        this._pageCursor++;
        this._matchCursor = 0;
        pagesScanned++;
        continue;
      }

      const pageMatches = searchPageWithIndex(
        page.entry.id,
        page.entry.name,
        page.pageIndex,
        entries,
        searchIndex,
        this._normalizedQuery,
      ).filter((match) => match.exactMatch === (this._priority === "exact"));

      const initialResultCount = matches.length;
      for (let i = this._matchCursor; i < pageMatches.length && matches.length < limit; i++) {
        matches.push(pageMatches[i]!);
      }

      const appendedFromPage = matches.length - initialResultCount;
      if (this._matchCursor + appendedFromPage < pageMatches.length) {
        this._matchCursor += appendedFromPage;
        break;
      }

      this._pageCursor++;
      this._matchCursor = 0;
      pagesScanned++;
    }

    return { matches, complete: this._complete, hasMore: !this._complete };
  }
}
