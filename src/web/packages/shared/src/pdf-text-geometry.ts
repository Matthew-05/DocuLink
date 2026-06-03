import * as pdfjsLib from "pdfjs-dist";
import type { CharacterEntry } from "./char-entries.js";
import type { TextGeometry, TextGeometryCharacter } from "./geometry-decoder.js";

pdfjsLib.GlobalWorkerOptions.workerSrc ||= "pdf.worker.min.mjs";

interface PdfTextItem {
  str: string;
  transform: number[];
  width: number;
  height: number;
  fontName: string;
}

function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary);
}

function isNewLine(prev: CharacterEntry, nextTop: number): boolean {
  const prevHeight = prev.normBottom - prev.normTop;
  const threshold = Math.max(prevHeight * 0.5, 0.001);
  return nextTop - prev.normTop > threshold;
}

function measureCharWidths(
  ctx: CanvasRenderingContext2D | null,
  chars: string[],
  fontSize: number,
  fontFamily: string,
): number[] {
  if (!ctx) return chars.map(() => 1);

  ctx.font = `${fontSize}px ${fontFamily}, sans-serif`;
  return chars.map((char) => ctx.measureText(char).width);
}

async function buildPageEntries(page: pdfjsLib.PDFPageProxy): Promise<CharacterEntry[]> {
  const viewport = page.getViewport({ scale: 1 });
  const textContent = await page.getTextContent();
  const entries: CharacterEntry[] = [];
  let itemIndex = 0;
  let lineIndex = 0;
  let lastEntry: CharacterEntry | null = null;

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

    const normLeft = vx / viewport.width;
    const normBottom = vy / viewport.height;
    const normRight = (vx + item.width) / viewport.width;
    const normTop = (vy - item.height) / viewport.height;

    const chars = [...item.str];
    if (chars.length === 0) {
      itemIndex++;
      continue;
    }

    const style = textContent.styles[item.fontName];
    const fontFamily = style?.fontFamily ?? "sans-serif";
    const fontSize = item.height;

    if (lastEntry && isNewLine(lastEntry, normTop)) {
      lineIndex++;
    }

    const charWidths = measureCharWidths(measureCtx, chars, fontSize, fontFamily);
    const totalMeasured = charWidths.reduce((sum, w) => sum + w, 0);
    const itemWidth = normRight - normLeft;
    const scale = totalMeasured > 0 ? itemWidth / totalMeasured : itemWidth / chars.length;

    let xOffset = 0;
    for (let i = 0; i < chars.length; i++) {
      const charWidth = totalMeasured > 0 ? (charWidths[i] ?? 0) * scale : itemWidth / chars.length;
      const entry: CharacterEntry = {
        char: chars[i] ?? "",
        normLeft: normLeft + xOffset,
        normTop,
        normRight: normLeft + xOffset + charWidth,
        normBottom,
        lineIndex,
        itemIndex,
        spacesPrecomputed: false,
      };
      entries.push(entry);
      lastEntry = entry;
      xOffset += charWidth;
    }

    itemIndex++;
  }

  return entries;
}

function entryToCharacter(entry: CharacterEntry): TextGeometryCharacter {
  return {
    char: entry.char,
    x: entry.normLeft,
    y: entry.normTop,
    width: entry.normRight - entry.normLeft,
    height: entry.normBottom - entry.normTop,
  };
}

export async function extractTextGeometryFromPdfDocument(
  doc: pdfjsLib.PDFDocumentProxy,
): Promise<TextGeometry> {
  const pages: TextGeometry["pages"] = [];

  for (let pageNum = 1; pageNum <= doc.numPages; pageNum++) {
    const page = await doc.getPage(pageNum);
    try {
      const entries = await buildPageEntries(page);
      pages.push({
        pageIndex: pageNum - 1,
        characters: entries.map(entryToCharacter),
      });
    } finally {
      page.cleanup();
    }
  }

  return { version: 1, coordinateSpace: "normalized", pages };
}

export async function extractTextGeometryFromPdfUrl(url: string): Promise<TextGeometry> {
  const doc = await pdfjsLib.getDocument(url).promise;
  try {
    return await extractTextGeometryFromPdfDocument(doc);
  } finally {
    doc.destroy();
  }
}

export async function extractTextGeometryFromPdfBase64(base64: string): Promise<TextGeometry> {
  const doc = await pdfjsLib.getDocument({ data: base64ToBytes(base64) }).promise;
  try {
    return await extractTextGeometryFromPdfDocument(doc);
  } finally {
    doc.destroy();
  }
}

export async function encodeTextGeometry(geometry: TextGeometry): Promise<string> {
  const json = JSON.stringify(geometry);
  const stream = new Blob([json]).stream().pipeThrough(new CompressionStream("gzip"));
  const compressed = new Uint8Array(await new Response(stream).arrayBuffer());
  return bytesToBase64(compressed);
}
