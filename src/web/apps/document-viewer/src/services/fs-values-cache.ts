import type { FinancialValue, FsValues } from "@doculink/shared";

export class FsValuesCache {
  private readonly _cache = new Map<string, Map<number, FinancialValue[]>>();
  private readonly _decoder: ((base64: string) => Promise<FsValues>) | undefined;

  constructor(decoder?: (base64: string) => Promise<FsValues>) {
    this._decoder = decoder;
  }

  async build(pdfId: string, fsValuesBase64?: string): Promise<void> {
    this.clearPdf(pdfId);
    if (!fsValuesBase64) {
      // Only analyzed/OCR documents publish this artifact. Native-PDF fallback
      // detection is deliberately a separate follow-up feature.
      this._cache.set(pdfId, new Map());
      return;
    }
    try {
      const decode = this._decoder ?? (await import("@doculink/shared")).decodeFsValues;
      const model = await decode(fsValuesBase64);
      this._cache.set(pdfId, new Map(model.pages.map((page) => [page.pageIndex, page.values])));
    } catch {
      // A malformed optional artifact must not prevent the PDF itself loading.
      this._cache.set(pdfId, new Map());
    }
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
