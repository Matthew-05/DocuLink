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
  if (viewer.getActivePdfId() !== pdfId) {
    const entry = selector.getEntry(pdfId);
    if (!entry) return;
    selector.setActiveId(pdfId);
    await viewer.loadDocument(entry.url, pdfId);
  }

  // page is 0-based; scrollToPage and [data-page] are 1-based.
  const pageWrapper = viewer.element.querySelector<HTMLElement>(
    `[data-page="${page + 1}"]`,
  );

  // Highlight first so the [data-rect-id] element is in the DOM before the visibility check.
  renderer.highlightRectangle(id);

  if (pageWrapper) {
    const rectEl      = viewer.element.querySelector<HTMLElement>(`[data-rect-id="${CSS.escape(id)}"]`);
    const viewerRect  = viewer.element.getBoundingClientRect();
    const targetRect  = rectEl?.getBoundingClientRect();
    const fullyVisible = targetRect
      && targetRect.top    >= viewerRect.top
      && targetRect.bottom <= viewerRect.bottom
      && targetRect.left   >= viewerRect.left
      && targetRect.right  <= viewerRect.right;

    if (!fullyVisible) {
      pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });
    }
  }
}
