import type { FinancialValue, FsValues } from "@doculink/shared";
import type { TextContentCache } from "./text-content-cache.js";

export class FsValuesCache {
  private readonly _cache = new Map<string, Map<number, FinancialValue[]>>();
  private readonly _decoder: ((base64: string) => Promise<FsValues>) | undefined;

  constructor(decoder?: (base64: string) => Promise<FsValues>) {
    this._decoder = decoder;
  }

  async build(pdfId: string, fsValuesBase64: string | undefined, textCache: TextContentCache): Promise<void> {
    this.clearPdf(pdfId);
    if (fsValuesBase64) {
      try {
        const decode = this._decoder ?? (await import("@doculink/shared")).decodeFsValues;
        const model = await decode(fsValuesBase64);
        this._cache.set(pdfId, new Map(model.pages.map((page) => [page.pageIndex, page.values])));
        return;
      } catch {
        // A stale/corrupt optional artifact falls through to deterministic browser detection.
      }
    }

    const pages = new Map<number, FinancialValue[]>();
    const { detectFsValuesFromEntries } = await import("@doculink/shared");
    for (const pageIndex of textCache.getPageIndices(pdfId)) {
      pages.set(pageIndex, detectFsValuesFromEntries(pageIndex, textCache.get(pdfId, pageIndex) ?? []).values);
    }
    this._cache.set(pdfId, pages);
  }

  valuesOnPage(pdfId: string, pageIndex: number): FinancialValue[] {
    return this._cache.get(pdfId)?.get(pageIndex) ?? [];
  }

  has(pdfId: string): boolean { return this._cache.has(pdfId); }

  valueCount(pdfId: string): number {
    let total = 0;
    for (const values of this._cache.get(pdfId)?.values() ?? []) total += values.length;
    return total;
  }

  clearPdf(pdfId: string): void { this._cache.delete(pdfId); }

  clear(): void { this._cache.clear(); }
}
