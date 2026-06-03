import { normalizeSearchQuery, searchPage } from "@doculink/shared";
import type { TextContentCache } from "../../services/text-content-cache.js";
import type { PdfEntry, SearchMatch } from "../../types/index.js";

export { normalizeSearchQuery } from "@doculink/shared";

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

        results.push(...searchPage(entry.id, entry.name, pageIndex, entries, normalizedQuery));
      }
    }

    return results;
  }
}
