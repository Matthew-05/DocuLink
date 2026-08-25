import {
  buildCharEntriesFromGeometry,
  buildSearchPageIndexFromEntries,
  decodeTextGeometry,
  encodeTextGeometry,
  extractText,
  extractTextGeometryFromPdfBase64,
  normalizeMatcherQuery,
  searchPageWithIndex,
} from "@doculink/shared";
import type { CharacterEntry, SearchPageIndex } from "@doculink/shared";
import type { LinkCreationRequest, MatcherPdf, MatcherRow, RowResult } from "./types/index.js";

interface PdfCache {
  id: string;
  name: string;
  pages: Map<number, MatcherPageCache>;
}

interface MatcherPageCache {
  entries: CharacterEntry[];
  searchIndex: SearchPageIndex;
}

function hasFiniteRect(rect: LinkCreationRequest["rect"]): boolean {
  return Number.isFinite(rect.x)
    && Number.isFinite(rect.y)
    && Number.isFinite(rect.width)
    && Number.isFinite(rect.height);
}

function buildMatcherPageCache(
  pages: Map<number, CharacterEntry[]>,
): Map<number, MatcherPageCache> {
  return new Map(Array.from(pages, ([pageIndex, entries]) => [
    pageIndex,
    {
      entries,
      searchIndex: buildSearchPageIndexFromEntries(entries, { normalizeDates: true }),
    },
  ]));
}

async function buildPdfCache(
  pdfs: MatcherPdf[],
  onGeometryPrepared?: (pdfId: string, geometryBase64: string) => void,
): Promise<PdfCache[]> {
  const result: PdfCache[] = [];
  for (const pdf of pdfs) {
    try {
      if (pdf.geometryBase64) {
        const geometry = await decodeTextGeometry(pdf.geometryBase64);
        result.push({
          id: pdf.id,
          name: pdf.name,
          pages: buildMatcherPageCache(buildCharEntriesFromGeometry(geometry)),
        });
        continue;
      }

      if (pdf.base64) {
        const geometry = await extractTextGeometryFromPdfBase64(pdf.base64);
        const geometryBase64 = await encodeTextGeometry(geometry);
        onGeometryPrepared?.(pdf.id, geometryBase64);
        result.push({
          id: pdf.id,
          name: pdf.name,
          pages: buildMatcherPageCache(buildCharEntriesFromGeometry(geometry)),
        });
      }
    } catch (error) {
      console.warn("[DocuLink] Skipping PDF with unreadable text content", pdf.name, error);
    }
  }
  return result;
}

function countMatchesInPdf(cache: PdfCache, normalizedTerms: string[]): number {
  let matched = 0;
  for (const term of normalizedTerms) {
    let found = false;
    for (const [pageIndex, page] of cache.pages) {
      if (searchPageWithIndex(
        cache.id,
        cache.name,
        pageIndex,
        page.entries,
        page.searchIndex,
        term,
      ).length > 0) {
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
  const page = cache.pages.get(pageIndex);
  if (!page) return 0;

  let count = 0;
  for (const term of normalizedTerms) {
    if (searchPageWithIndex(
      cache.id,
      cache.name,
      pageIndex,
      page.entries,
      page.searchIndex,
      term,
    ).length > 0) count++;
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
  onGeometryPrepared?: (pdfId: string, geometryBase64: string) => void,
): Promise<LinkCreationRequest[]> {
  const pdfCache = await buildPdfCache(pdfs, onGeometryPrepared);
  const requests: LinkCreationRequest[] = [];

  for (const row of rows) {
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));

    const normalizedTerms = row.keyValues.map((v) => normalizeMatcherQuery(v ?? "")).filter((t) => t.length > 0);

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
    const bestPage = bestPdf.pages.get(bestPageIndex);
    if (!bestPage) {
      onRowComplete({ rowIndex: row.rowIndex, status: "unmatched", linkCount: 0 });
      continue;
    }

    const pageEntries = bestPage.entries;
    let linkCount = 0;

    for (let i = 0; i < row.keyValues.length; i++) {
      const outputColNumber = outputColNumbers[i];
      if (outputColNumber === undefined) continue;

      const rawValue = row.keyValues[i] ?? "";
      if (!rawValue) continue;

      const normalized = normalizeMatcherQuery(rawValue);
      if (!normalized) continue;

      const matches = searchPageWithIndex(
        bestPdf.id,
        bestPdf.name,
        bestPageIndex,
        pageEntries,
        bestPage.searchIndex,
        normalized,
      );
      if (matches.length === 0) continue;

      const match = matches[0]!;
      if (!hasFiniteRect(match.highlightRect)) continue;

      const extractedText = extractText(pageEntries, match.highlightRect);
      if (extractedText.length === 0) {
        console.warn(
          "[DocuLink] Skipping matcher link because matched rectangle extracted no text",
          {
            pdfName: bestPdf.name,
            pageIndex: bestPageIndex,
            rowIndex: row.rowIndex,
            outputColNumber,
            contextText: match.contextText,
          },
        );
        continue;
      }

      requests.push({
        rowIndex: row.rowIndex,
        outputColNumber,
        pdfId: bestPdf.id,
        pageIndex: bestPageIndex,
        rect: match.highlightRect,
        text: extractedText,
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
