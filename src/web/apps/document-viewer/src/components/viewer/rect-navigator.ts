import type { PdfViewer } from "./pdf-viewer.js";
import type { PdfSelector } from "../toolbar/pdf-selector.js";
import type { RectRenderer } from "./rect-renderer.js";

/**
 * Returns a handler that navigates the viewer to a specific link rectangle.
 *
 * - If the rectangle's PDF is already active, jumps instantly to the page and
 *   highlights the rectangle.
 * - If a different PDF is required, switches to it first (awaiting full render),
 *   then jumps and highlights.
 */
export function createRectNavigator(
  viewer: PdfViewer,
  selector: PdfSelector,
  renderer: RectRenderer,
): (id: string, pdfId: string, page: number) => void {
  return (id, pdfId, page) => {
    void _navigate(viewer, selector, renderer, id, pdfId, page);
  };
}

async function _navigate(
  viewer: PdfViewer,
  selector: PdfSelector,
  renderer: RectRenderer,
  id: string,
  pdfId: string,
  page: number,
): Promise<void> {
  // Ignore stale navigate messages (e.g. queued before a ribbon bulk delete).
  if (!renderer.hasRectangle(id)) return;

  if (viewer.getActivePdfId() !== pdfId) {
    const entry = selector.getEntry(pdfId);
    if (!entry) return;
    selector.setActiveId(pdfId);
    await viewer.loadDocument(entry.url, pdfId, page + 1);
  }

  if (!renderer.hasRectangle(id)) return;

  const rectEl = viewer.element.querySelector<HTMLElement>(
    `[data-rect-id="${CSS.escape(id)}"]`,
  );
  if (!rectEl) return;

  renderer.highlightRectangle(id);

  const pageWrapper = viewer.element.querySelector<HTMLElement>(
    `[data-page="${page + 1}"]`,
  );
  if (!pageWrapper) return;

  const viewerRect  = viewer.element.getBoundingClientRect();
  const targetRect  = rectEl.getBoundingClientRect();
  const fullyVisible = targetRect.top    >= viewerRect.top
    && targetRect.bottom <= viewerRect.bottom
    && targetRect.left   >= viewerRect.left
    && targetRect.right  <= viewerRect.right;

  if (!fullyVisible) {
    pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });
  }
}
