import type { FinancialValue, FsValues } from "@doculink/shared";

export class FsValuesCache {
  private readonly _cache = new Map<string, Map<number, FinancialValue[]>>();
  private readonly _logicalValues = new Map<string, Map<number, FinancialValue[]>>();
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
      this._logicalValues.set(pdfId, new Map());
      return;
    }
    try {
      const decode = this._decoder ?? (await import("@doculink/shared")).decodeFsValues;
      const model = await decode(fsValuesBase64);
      const byPage = new Map<number, FinancialValue[]>();
      const logicalValues = new Map<number, FinancialValue[]>();
      for (const page of model.pages) {
        for (const value of page.values) {
          const pageLogicalValues = logicalValues.get(page.pageIndex) ?? [];
          pageLogicalValues.push(value);
          logicalValues.set(page.pageIndex, pageLogicalValues);
          const segments = value.segments ?? [{ pageIndex: page.pageIndex, text: value.text, bounds: value.bounds }];
          for (const segment of segments) {
            const pageValues = byPage.get(segment.pageIndex) ?? [];
            pageValues.push({ ...value, bounds: segment.bounds });
            byPage.set(segment.pageIndex, pageValues);
          }
        }
      }
      this._cache.set(pdfId, byPage);
      this._logicalValues.set(pdfId, logicalValues);
    } catch {
      // A malformed optional artifact must not prevent the PDF itself loading.
      this._cache.set(pdfId, new Map());
      this._logicalValues.set(pdfId, new Map());
    }
  }

  valuesOnPage(pdfId: string, pageIndex: number): FinancialValue[] {
    return this._cache.get(pdfId)?.get(pageIndex) ?? [];
  }

  has(pdfId: string): boolean { return this._cache.has(pdfId); }

  valueCount(pdfId: string): number {
    let total = 0;
    for (const values of this._logicalValues.get(pdfId)?.values() ?? []) total += values.length;
    return total;
  }

  logicalValuesOnPage(pdfId: string, pageIndex: number): FinancialValue[] {
    return this._logicalValues.get(pdfId)?.get(pageIndex) ?? [];
  }

  clearPdf(pdfId: string): void {
    this._cache.delete(pdfId);
    this._logicalValues.delete(pdfId);
  }

  clear(): void {
    this._cache.clear();
    this._logicalValues.clear();
  }
}
