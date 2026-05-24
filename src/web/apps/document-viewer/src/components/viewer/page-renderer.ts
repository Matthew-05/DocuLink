import type * as pdfjsLib from "pdfjs-dist";
import type { ZoomLevel } from "../../types/index.js";

export interface PageBaseDimensions {
  baseWidth: number;
  baseHeight: number;
}

/**
 * Renders a single PDF page into the given wrapper div.
 *
 * Creates a new canvas, renders into it while the old canvas remains visible,
 * then atomically swaps in the new canvas via replaceWith — the wrapper never
 * shows a blank state.
 *
 * Also updates the wrapper's inline width/height to match the rendered viewport
 * and returns the intrinsic page dimensions at scale=1 so the caller can
 * resize wrappers instantly on future zoom changes without re-rendering.
 */
export async function renderPage(
  doc: pdfjsLib.PDFDocumentProxy,
  pageNumber: number,
  wrapper: HTMLDivElement,
  scale: ZoomLevel
): Promise<PageBaseDimensions> {
  const page = await doc.getPage(pageNumber);
  const viewport = page.getViewport({ scale });

  const canvas = document.createElement("canvas");
  canvas.className = "viewer__canvas";
  canvas.width = viewport.width;
  canvas.height = viewport.height;

  const ctx = canvas.getContext("2d");
  if (!ctx) {
    page.cleanup();
    return { baseWidth: viewport.width / scale, baseHeight: viewport.height / scale };
  }

  try {
    await page.render({ canvasContext: ctx, viewport }).promise;
  } catch (e) {
    page.cleanup();
    // RenderingCancelledException is thrown when the PDF proxy is destroyed while
    // a render is in flight (e.g. a second navigation loads a different PDF).
    // Treat it as a clean cancellation: return dimensions but don't swap the canvas.
    if ((e as { name?: string })?.name === "RenderingCancelledException") {
      return { baseWidth: viewport.width / scale, baseHeight: viewport.height / scale };
    }
    throw e;
  }
  page.cleanup();

  // Update wrapper layout dimensions to match the new viewport.
  wrapper.style.width = `${viewport.width}px`;
  wrapper.style.height = `${viewport.height}px`;

  // Atomic swap: old canvas stays visible until this synchronous call.
  // The new canvas only appears once fully rendered.
  //
  // When inserting fresh (no old canvas), place the canvas BEFORE any existing
  // overlay layer. The canvas has contain:strict which creates a stacking context
  // at z-index:0; the overlay (position:absolute, z-index:auto) must come later
  // in DOM order to paint on top of the canvas and keep rectangles visible.
  const old = wrapper.querySelector("canvas");
  if (old) {
    old.replaceWith(canvas);
  } else {
    const overlay = wrapper.querySelector<HTMLDivElement>(".viewer__overlays");
    if (overlay) {
      wrapper.insertBefore(canvas, overlay);
    } else {
      wrapper.appendChild(canvas);
    }
  }

  ensureOverlayLayer(wrapper);

  return { baseWidth: viewport.width / scale, baseHeight: viewport.height / scale };
}

/** Ensures a dedicated overlay layer exists above the canvas. */
export function ensureOverlayLayer(wrapper: HTMLDivElement): HTMLDivElement {
  let layer = wrapper.querySelector<HTMLDivElement>(".viewer__overlays");
  if (!layer) {
    layer = document.createElement("div");
    layer.className = "viewer__overlays";
    wrapper.appendChild(layer);
  }
  return layer;
}
