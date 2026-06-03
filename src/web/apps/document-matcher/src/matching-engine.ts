import {
  buildCharEntriesFromGeometry,
  decodeTextGeometry,
  normalizeSearchQuery,
  searchPage,
} from "@doculink/shared";
import type { CharacterEntry } from "@doculink/shared";
import type { LinkCreationRequest, MatcherPdf, MatcherRow, RowResult } from "./types/index.js";

interface PdfCache {
  id: string;
  name: string;
  pages: Map<number, CharacterEntry[]>;
}

async function buildPdfCache(pdfs: MatcherPdf[]): Promise<PdfCache[]> {
  const result: PdfCache[] = [];
  for (const pdf of pdfs) {
    if (!pdf.geometryBase64) continue;
    const geometry = await decodeTextGeometry(pdf.geometryBase64);
    result.push({ id: pdf.id, name: pdf.name, pages: buildCharEntriesFromGeometry(geometry) });
  }
  return result;
}

function countMatchesInPdf(cache: PdfCache, normalizedTerms: string[]): number {
  let matched = 0;
  for (const term of normalizedTerms) {
    let found = false;
    for (const [pageIndex, entries] of cache.pages) {
      if (searchPage(cache.id, cache.name, pageIndex, entries, term).length > 0) {
        found = true;
        break;
      }
    }
    if (found) matched++;
  }
  return matched;
}

function countMatchesOnPage(
  cache: PdfCache,
  pageIndex: number,
  normalizedTerms: string[],
): number {
  const entries = cache.pages.get(pageIndex) ?? [];
  let count = 0;
  for (const term of normalizedTerms) {
    if (searchPage(cache.id, cache.name, pageIndex, entries, term).length > 0) count++;
  }
  return count;
}

/**
 * Runs the matching algorithm for all rows.
 *
 * @param pdfs - PDFs to search, with pre-loaded geometry.
 * @param rows - Data rows; each row's keyValues[i] corresponds to outputColNumbers[i].
 * @param outputColNumbers - 1-based Excel column numbers for each key column's output cell.
 * @param onRowComplete - Callback fired after each row is processed (for progress updates).
 */
export async function runMatching(
  pdfs: MatcherPdf[],
  rows: MatcherRow[],
  outputColNumbers: number[],
  onRowComplete: (result: RowResult) => void,
): Promise<LinkCreationRequest[]> {
  const pdfCache = await buildPdfCache(pdfs);
  const requests: LinkCreationRequest[] = [];

  for (const row of rows) {
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));

    const normalizedTerms = row.keyValues.map((v) => normalizeSearchQuery(v ?? "")).filter((t) => t.length > 0);

    if (normalizedTerms.length === 0) {
      onRowComplete({ rowIndex: row.rowIndex, status: "skipped", linkCount: 0 });
      continue;
    }

    // Score each PDF by how many key terms appear anywhere in it
    let bestPdf: PdfCache | null = null;
    let bestPdfScore = 0;
    for (const cache of pdfCache) {
      const score = countMatchesInPdf(cache, normalizedTerms);
      if (score > bestPdfScore) {
        bestPdfScore = score;
        bestPdf = cache;
      }
    }

    if (bestPdf === null || bestPdfScore === 0) {
      onRowComplete({ rowIndex: row.rowIndex, status: "unmatched", linkCount: 0 });
      continue;
    }

    // Score each page in the best PDF
    let bestPageIndex = 0;
    let bestPageScore = -1;
    for (const pageIndex of bestPdf.pages.keys()) {
      const score = countMatchesOnPage(bestPdf, pageIndex, normalizedTerms);
      if (score > bestPageScore) {
        bestPageScore = score;
        bestPageIndex = pageIndex;
      }
    }

    // Build one link creation request per key column that has a match on the best page
    const pageEntries = bestPdf.pages.get(bestPageIndex) ?? [];
    let linkCount = 0;

    for (let i = 0; i < row.keyValues.length; i++) {
      const outputColNumber = outputColNumbers[i];
      if (outputColNumber === undefined) continue;

      const rawValue = row.keyValues[i] ?? "";
      if (!rawValue) continue;

      const normalized = normalizeSearchQuery(rawValue);
      if (!normalized) continue;

      const matches = searchPage(bestPdf.id, bestPdf.name, bestPageIndex, pageEntries, normalized);
      if (matches.length === 0) continue;

      const match = matches[0]!;
      requests.push({
        rowIndex: row.rowIndex,
        outputColNumber,
        pdfId: bestPdf.id,
        pageIndex: bestPageIndex,
        rect: match.highlightRect,
        text: match.contextText,
      });
      linkCount++;
    }

    onRowComplete({
      rowIndex: row.rowIndex,
      status: linkCount > 0 ? "matched" : "unmatched",
      pdfName: bestPdf.name,
      linkCount,
    });
  }

  return requests;
}
