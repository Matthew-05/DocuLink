import type * as pdfjsLib from "pdfjs-dist";
import type { ZoomLevel } from "../../types/index.js";

/**
 * Renders a single PDF page onto the provided canvas at the given scale.
 */
export async function renderPage(
  doc: pdfjsLib.PDFDocumentProxy,
  pageNumber: number,
  canvas: HTMLCanvasElement,
  scale: ZoomLevel
): Promise<void> {
  const page = await doc.getPage(pageNumber);
  const viewport = page.getViewport({ scale });

  canvas.width = viewport.width;
  canvas.height = viewport.height;

  const ctx = canvas.getContext("2d");
  if (!ctx) return;

  await page.render({ canvasContext: ctx, viewport }).promise;
  page.cleanup();
}
