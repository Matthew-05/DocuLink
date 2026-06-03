import { buildCharEntriesFromGeometry, decodeTextGeometry, extractTextGeometryFromPdfDocument, extractTextGeometryFromPdfUrl } from "@doculink/shared";
import * as pdfjsLib from "pdfjs-dist";

export interface CharacterEntry {
  char: string;
  normLeft: number;
  normTop: number;
  normRight: number;
  normBottom: number;
  /** 0-based visual line index within the page. */
  lineIndex: number;
  /** Index of the source TextItem within the page's text content. */
  itemIndex: number;
  /** When true, word spaces are already encoded as space characters in the array. */
  spacesPrecomputed?: boolean;
}

export class TextContentCache {
  private readonly _cache = new Map<string, Map<number, CharacterEntry[]>>();
  private readonly _geometryByPdfId = new Map<string, string>();

  /**
   * Builds the full character cache for every page by loading the PDF from
   * the given URL. Opens and destroys its own pdf.js document handle.
   * Skips work when the pdfId is already indexed.
   */
  async buildForUrl(
    pdfId: string,
    url: string,
    geometryBase64?: string,
  ): Promise<void> {
    if (geometryBase64 !== undefined) {
      if (geometryBase64) {
        this._geometryByPdfId.set(pdfId, geometryBase64);
      } else {
        this._geometryByPdfId.delete(pdfId);
      }
    }

    if (this.has(pdfId)) return;

    const storedGeometry = this._geometryByPdfId.get(pdfId);
    if (storedGeometry) {
      await this._buildFromGeometry(pdfId, storedGeometry);
      return;
    }

    const geometry = await extractTextGeometryFromPdfUrl(url);
    this._cache.set(pdfId, buildCharEntriesFromGeometry(geometry));
  }

  /**
   * Builds the cache from an already-open document owned by the viewer.
   * Used as a fallback when the active PDF was not indexed yet.
   */
  async buildFromDoc(pdfId: string, doc: pdfjsLib.PDFDocumentProxy): Promise<void> {
    if (this.has(pdfId)) return;

    const storedGeometry = this._geometryByPdfId.get(pdfId);
    if (storedGeometry) {
      await this._buildFromGeometry(pdfId, storedGeometry);
      return;
    }

    const geometry = await extractTextGeometryFromPdfDocument(doc);
    this._cache.set(pdfId, buildCharEntriesFromGeometry(geometry));
  }

  /** Returns true when every page of the PDF has been indexed. */
  has(pdfId: string): boolean {
    return this._cache.has(pdfId);
  }

  /** Returns all indexed pdfIds. */
  getIndexedPdfIds(): string[] {
    return Array.from(this._cache.keys());
  }

  /** Returns sorted 0-based page indices for an indexed PDF. */
  getPageIndices(pdfId: string): number[] {
    const pageMap = this._cache.get(pdfId);
    if (!pageMap) return [];
    return Array.from(pageMap.keys()).sort((a, b) => a - b);
  }

  /** Synchronous lookup. Returns null if the page cache was never built. */
  get(pdfId: string, pageIndex: number): CharacterEntry[] | null {
    return this._cache.get(pdfId)?.get(pageIndex) ?? null;
  }

  /** Removes a single PDF from the cache (e.g. after OCR update). */
  clearPdf(pdfId: string): void {
    this._cache.delete(pdfId);
  }

  clear(): void {
    this._cache.clear();
    this._geometryByPdfId.clear();
  }

  private async _buildFromGeometry(pdfId: string, geometryBase64: string): Promise<void> {
    const geometry = await decodeTextGeometry(geometryBase64);
    this._cache.set(pdfId, buildCharEntriesFromGeometry(geometry));
  }
}
