import * as pdfjsLib from "pdfjs-dist";

export interface CharacterEntry {
  char: string;
  normLeft: number;
  normTop: number;
  normRight: number;
  normBottom: number;
  /** Index of the source TextItem within the page's text content. */
  itemIndex: number;
}

export class TextContentCache {
  private readonly _cache = new Map<string, Map<number, CharacterEntry[]>>();

  /**
   * Builds the full character cache for every page by loading the PDF from
   * the given URL. Opens and destroys its own pdf.js document handle.
   * Skips work when the pdfId is already indexed.
   */
  async buildForUrl(pdfId: string, url: string): Promise<void> {
    if (this.has(pdfId)) return;

    const doc = await pdfjsLib.getDocument(url).promise;
    try {
      await this._buildFromDoc(pdfId, doc);
    } finally {
      doc.destroy();
    }
  }

  /**
   * Builds the cache from an already-open document owned by the viewer.
   * Used as a fallback when the active PDF was not indexed yet.
   */
  async buildFromDoc(pdfId: string, doc: pdfjsLib.PDFDocumentProxy): Promise<void> {
    if (this.has(pdfId)) return;
    await this._buildFromDoc(pdfId, doc);
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
  }

  private async _buildFromDoc(
    pdfId: string,
    doc: pdfjsLib.PDFDocumentProxy,
  ): Promise<void> {
    const pageMap = new Map<number, CharacterEntry[]>();

    for (let pageNum = 1; pageNum <= doc.numPages; pageNum++) {
      const page = await doc.getPage(pageNum);
      try {
        pageMap.set(pageNum - 1, await buildPageEntries(page));
      } finally {
        page.cleanup();
      }
    }

    this._cache.set(pdfId, pageMap);
  }
}

async function buildPageEntries(page: pdfjsLib.PDFPageProxy): Promise<CharacterEntry[]> {
  // scale: 1 so that user-space units equal viewport pixels, making
  // normalisation (divide by viewport.width/height) straightforward.
  const viewport = page.getViewport({ scale: 1 });
  const textContent = await page.getTextContent();
  const entries: CharacterEntry[] = [];
  let itemIndex = 0;

  for (const raw of textContent.items) {
    // TextMarkedContent items lack a `str` property — skip them.
    if (!("str" in raw) || !raw.str) {
      itemIndex++;
      continue;
    }

    const item = raw as pdfjsLib.TextItem;
    const tx = item.transform[4];
    const ty = item.transform[5];

    // convertToViewportPoint handles y-flip and any page rotation.
    // Result: viewport-space coords where y increases downward from top.
    const [vx, vy] = viewport.convertToViewportPoint(tx, ty);

    // vy is the text baseline. Text ascends upward by item.height
    // (at scale=1, user-space height == viewport pixels).
    const normLeft   = vx / viewport.width;
    const normBottom = vy / viewport.height;
    const normRight  = (vx + item.width) / viewport.width;
    const normTop    = (vy - item.height) / viewport.height;

    // Unicode-safe character split handles multi-byte glyphs correctly.
    const chars = [...item.str];
    const charWidth = chars.length > 0
      ? (normRight - normLeft) / chars.length
      : 0;

    for (let i = 0; i < chars.length; i++) {
      entries.push({
        char:       chars[i],
        normLeft:   normLeft + i * charWidth,
        normTop,
        normRight:  normLeft + (i + 1) * charWidth,
        normBottom,
        itemIndex,
      });
    }

    itemIndex++;
  }

  return entries;
}
