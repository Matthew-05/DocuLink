import type * as pdfjsLib from "pdfjs-dist";

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
   * Builds the full character cache for every page of the given document.
   * Called once when a document loads; subsequent draws on any page are
   * purely synchronous.
   */
  async buildAll(pdfId: string, doc: pdfjsLib.PDFDocumentProxy): Promise<void> {
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

  /** Synchronous lookup. Returns null if the page cache was never built. */
  get(pdfId: string, pageIndex: number): CharacterEntry[] | null {
    return this._cache.get(pdfId)?.get(pageIndex) ?? null;
  }

  clear(): void {
    this._cache.clear();
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
