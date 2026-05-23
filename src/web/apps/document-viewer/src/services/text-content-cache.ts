import * as pdfjsLib from "pdfjs-dist";
import { decodeTextGeometry } from "./geometry-decoder.js";

interface PdfTextItem {
  str: string;
  transform: number[];
  width: number;
  height: number;
  fontName: string;
}

export interface CharacterEntry {
  char: string;
  normLeft: number;
  normTop: number;
  normRight: number;
  normBottom: number;
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

    const storedGeometry = this._geometryByPdfId.get(pdfId);
    if (storedGeometry) {
      await this._buildFromGeometry(pdfId, storedGeometry);
      return;
    }

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
    this._geometryByPdfId.clear();
  }

  private async _buildFromGeometry(pdfId: string, geometryBase64: string): Promise<void> {
    const geometry = await decodeTextGeometry(geometryBase64);
    const pageMap = new Map<number, CharacterEntry[]>();

    for (const page of geometry.pages) {
      const entries: CharacterEntry[] = page.characters.map((box, index) => ({
        char:               box.char,
        normLeft:           box.x,
        normTop:            box.y,
        normRight:          box.x + box.width,
        normBottom:         box.y + box.height,
        itemIndex:          index,
        spacesPrecomputed:    true,
      }));
      pageMap.set(page.pageIndex, entries);
    }

    this._cache.set(pdfId, pageMap);
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
  const viewport = page.getViewport({ scale: 1 });
  const textContent = await page.getTextContent();
  const entries: CharacterEntry[] = [];
  let itemIndex = 0;

  const measureCtx = document.createElement("canvas").getContext("2d");

  for (const raw of textContent.items) {
    if (!("str" in raw) || !raw.str) {
      itemIndex++;
      continue;
    }

    const item = raw as PdfTextItem;
    const tx = item.transform[4];
    const ty = item.transform[5];
    const [vx, vy] = viewport.convertToViewportPoint(tx, ty);

    const normLeft   = vx / viewport.width;
    const normBottom = vy / viewport.height;
    const normRight  = (vx + item.width) / viewport.width;
    const normTop    = (vy - item.height) / viewport.height;

    const chars = [...item.str];
    if (chars.length === 0) {
      itemIndex++;
      continue;
    }

    const style = textContent.styles[item.fontName];
    const fontFamily = style?.fontFamily ?? "sans-serif";
    const fontSize = item.height;

    const charWidths = measureCharWidths(measureCtx, chars, fontSize, fontFamily);
    const totalMeasured = charWidths.reduce((sum, w) => sum + w, 0);
    const itemWidth = normRight - normLeft;
    const scale = totalMeasured > 0 ? itemWidth / totalMeasured : itemWidth / chars.length;

    let xOffset = 0;
    for (let i = 0; i < chars.length; i++) {
      const charWidth = totalMeasured > 0 ? (charWidths[i] ?? 0) * scale : itemWidth / chars.length;
      entries.push({
        char:               chars[i],
        normLeft:           normLeft + xOffset,
        normTop,
        normRight:          normLeft + xOffset + charWidth,
        normBottom,
        itemIndex,
        spacesPrecomputed:  false,
      });
      xOffset += charWidth;
    }

    itemIndex++;
  }

  return entries;
}

function measureCharWidths(
  ctx: CanvasRenderingContext2D | null,
  chars: string[],
  fontSize: number,
  fontFamily: string,
): number[] {
  if (!ctx) {
    return chars.map(() => 1);
  }

  ctx.font = `${fontSize}px ${fontFamily}, sans-serif`;
  return chars.map((char) => ctx.measureText(char).width);
}
