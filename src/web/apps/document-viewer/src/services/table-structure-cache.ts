import type { DetectedTable, TableStructure } from "@doculink/shared";
import type { NormalizedRect } from "../types/index.js";

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

  tablesOnPage(pdfId: string, pageIndex: number): DetectedTable[] {
    return this._cache.get(pdfId)?.get(pageIndex) ?? [];
  }

  tableAt(pdfId: string, pageIndex: number, rect: NormalizedRect): DetectedTable | null {
    const centerX = rect.x + rect.width / 2;
    const centerY = rect.y + rect.height / 2;
    const matches = this.tablesOnPage(pdfId, pageIndex).filter((table) => {
      const bounds = table.bounds;
      return centerX >= bounds.x && centerX <= bounds.x + bounds.width
        && centerY >= bounds.y && centerY <= bounds.y + bounds.height;
    });
    return matches.sort((first, second) =>
      first.bounds.width * first.bounds.height - second.bounds.width * second.bounds.height,
    )[0] ?? null;
  }

  clearPdf(pdfId: string): void {
    this._cache.delete(pdfId);
  }

  clear(): void {
    this._cache.clear();
  }
}
