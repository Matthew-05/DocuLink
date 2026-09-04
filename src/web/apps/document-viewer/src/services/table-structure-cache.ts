import type { DetectedTable, TableStructure } from "@doculink/shared";
import type { NormalizedRect } from "../types/index.js";

// A selection has to sit meaningfully inside a table before it counts as that
// table's; grazing its edge by a hair does not.
const MINIMUM_COVERAGE = 0.05;
// Two candidates whose coverage differs by less than this are treated as equally
// good, so the confidence the detector published decides between them.
const COVERAGE_TIE = 0.02;
const CONFIDENCE_TIE = 0.01;

function boundsArea(table: DetectedTable): number {
  return table.bounds.width * table.bounds.height;
}

function coverageOf(rect: NormalizedRect, table: DetectedTable, area: number): number {
  const bounds = table.bounds;
  const left = Math.max(Math.min(rect.x, rect.x + rect.width), bounds.x);
  const top = Math.max(Math.min(rect.y, rect.y + rect.height), bounds.y);
  const right = Math.min(Math.max(rect.x, rect.x + rect.width), bounds.x + bounds.width);
  const bottom = Math.min(Math.max(rect.y, rect.y + rect.height), bounds.y + bounds.height);
  const overlap = Math.max(0, right - left) * Math.max(0, bottom - top);
  if (area > 0) return overlap / area;
  // A zero-area selection is a click: it belongs to a table when it lands inside.
  return right >= left && bottom >= top ? 1 : 0;
}

export class TableStructureCache {
  private readonly _cache = new Map<string, Map<number, DetectedTable[]>>();
  private readonly _decoder: ((base64: string) => Promise<TableStructure>) | undefined;

  constructor(decoder?: (base64: string) => Promise<TableStructure>) {
    this._decoder = decoder;
  }

  async build(pdfId: string, tableStructureBase64?: string): Promise<void> {
    this.clearPdf(pdfId);
    if (!tableStructureBase64) return;
    try {
      const decode = this._decoder
        ?? (await import("@doculink/shared")).decodeTableStructure;
      const structure = await decode(tableStructureBase64);
      this._cache.set(
        pdfId,
        new Map(structure.pages.map((page) => [page.pageIndex, page.tables])),
      );
    } catch {
      // The model is optional. A corrupt or future-version payload must not prevent
      // the PDF and its ordinary text geometry from loading.
      this.clearPdf(pdfId);
    }
  }

  /** Tables detected across every page of one document. */
  tableCount(pdfId: string): number {
    let total = 0;
    for (const tables of this._cache.get(pdfId)?.values() ?? []) total += tables.length;
    return total;
  }

  tablesOnPage(pdfId: string, pageIndex: number): DetectedTable[] {
    return this._cache.get(pdfId)?.get(pageIndex) ?? [];
  }

  /**
   * The table a selection belongs to.
   *
   * Selections are dragged, not clicked, so the question is which table best
   * explains the rectangle rather than which one happens to contain its centre.
   * Centre containment picked the wrong neighbour whenever a selection began in
   * the whitespace above a table or straddled two adjacent ones, and "smallest
   * area wins" then preferred whichever fragment was smaller regardless of how
   * little of the selection it covered. Overlap decides; confidence breaks ties;
   * area and id keep the result stable for identical candidates.
   */
  tableAt(pdfId: string, pageIndex: number, rect: NormalizedRect): DetectedTable | null {
    const tables = this.tablesOnPage(pdfId, pageIndex);
    if (tables.length === 0) return null;
    const area = Math.abs(rect.width * rect.height);
    const scored = tables
      .map((table) => ({ table, coverage: coverageOf(rect, table, area) }))
      .filter((entry) => entry.coverage > MINIMUM_COVERAGE);
    if (scored.length === 0) return null;
    scored.sort((first, second) => {
      if (Math.abs(first.coverage - second.coverage) > COVERAGE_TIE) {
        return second.coverage - first.coverage;
      }
      const confidence = (second.table.confidence ?? 0) - (first.table.confidence ?? 0);
      if (Math.abs(confidence) > CONFIDENCE_TIE) return confidence;
      const size = boundsArea(first.table) - boundsArea(second.table);
      if (size !== 0) return size;
      return first.table.id < second.table.id ? -1 : first.table.id > second.table.id ? 1 : 0;
    });
    return scored[0].table;
  }

  clearPdf(pdfId: string): void {
    this._cache.delete(pdfId);
  }

  clear(): void {
    this._cache.clear();
  }
}
