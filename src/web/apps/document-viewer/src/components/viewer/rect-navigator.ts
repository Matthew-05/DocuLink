import type { ZoomLevel } from "../../types/index.js";
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
 * - If the rectangle is not fully visible, zoom is set to page-fit first.
 * - Updates the page controller to reflect the current page.
 */
export function createRectNavigator(
  viewer: PdfViewer,
  selector: PdfSelector,
  renderer: RectRenderer,
  applyZoom: (scale: ZoomLevel) => void,
  onNavigateToPage?: (pageNumber: number) => void,
): (id: string, pdfId: string, page: number) => void {
  return (id, pdfId, page) => {
    void _navigate(viewer, selector, renderer, applyZoom, id, pdfId, page, onNavigateToPage);
  };
}

async function _navigate(
  viewer: PdfViewer,
  selector: PdfSelector,
  renderer: RectRenderer,
  applyZoom: (scale: ZoomLevel) => void,
  id: string,
  pdfId: string,
  page: number,
  onNavigateToPage?: (pageNumber: number) => void,
): Promise<void> {
  console.log(`[RectNavigator] _navigate called: id=${id}, pdfId=${pdfId}, page=${page}`);
  console.log(`[RectNavigator] Current activePdfId: ${viewer.getActivePdfId()}`);

  const crossPdf = viewer.getActivePdfId() !== pdfId;
  console.log(`[RectNavigator] crossPdf=${crossPdf}`);

  if (crossPdf) {
    console.log(`[RectNavigator] Cross-PDF: loading ${pdfId} at page ${page + 1}`);
    const entry = selector.getEntry(pdfId);
    if (!entry) {
      console.log(`[RectNavigator] No entry found for ${pdfId}, aborting`);
      return;
    }
    selector.setActiveId(pdfId);
    await viewer.loadDocument(entry.url, pdfId, page + 1);
    console.log(`[RectNavigator] Cross-PDF load complete`);
  } else {
    console.log(`[RectNavigator] Same-PDF: waiting for load to complete`);
    await viewer.waitForLoad();
    console.log(`[RectNavigator] Load complete, proceeding to navigate`);
  }

  console.log(`[RectNavigator] Checking if rectangle ${id} exists in renderer`);
  if (!renderer.hasRectangle(id)) {
    console.log(`[RectNavigator] Rectangle ${id} not found in renderer, aborting`);
    return;
  }
  console.log(`[RectNavigator] Rectangle found`);

  console.log(`[RectNavigator] Querying for rect element with data-rect-id="${id}"`);
  const rectEl = viewer.element.querySelector<HTMLElement>(
    `[data-rect-id="${CSS.escape(id)}"]`,
  );
  if (!rectEl) {
    console.log(`[RectNavigator] Rect element not found in DOM for id=${id}`);
    return;
  }
  console.log(`[RectNavigator] Rect element found, highlighting`);

  renderer.highlightRectangle(id);

  console.log(`[RectNavigator] Querying for page wrapper with data-page="${page + 1}"`);
  const pageWrapper = viewer.element.querySelector<HTMLDivElement>(
    `[data-page="${page + 1}"]`,
  );
  if (!pageWrapper) {
    console.log(`[RectNavigator] Page wrapper not found for page ${page + 1}`);
    return;
  }
  console.log(`[RectNavigator] Page wrapper found`);

  if (crossPdf) {
    console.log(`[RectNavigator] Cross-PDF jump to id=${id}, pdfId=${pdfId}, page=${page + 1}`);
    const fitScale = viewer.getPageFitScale(page + 1);
    if (fitScale !== null) {
      console.log(`[RectNavigator] Applying fit scale: ${fitScale}`);
      applyZoom(fitScale);
    } else {
      console.log(`[RectNavigator] getPageFitScale returned null, skipping zoom`);
    }
    pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });
  } else {
    // Check if the target page has been rendered (has a canvas).
    // If not, this is first-time viewing, so apply fit-page zoom.
    const pageCanvas = pageWrapper.querySelector('canvas');
    const isPageUnrendered = !pageCanvas;

    if (isPageUnrendered) {
      console.log(`[RectNavigator] Same-PDF jump to id=${id}, page=${page + 1}, page not yet rendered, applying fit zoom`);
      const fitScale = viewer.getPageFitScale(page + 1);
      if (fitScale !== null) {
        console.log(`[RectNavigator] Applying fit scale: ${fitScale}`);
        applyZoom(fitScale);
      }
      pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });
    } else {
      // Page is already rendered; only zoom if rect is not fully visible
      const viewerRect = viewer.element.getBoundingClientRect();
      const targetRect = rectEl.getBoundingClientRect();
      const fullyVisible = targetRect.top    >= viewerRect.top
        && targetRect.bottom <= viewerRect.bottom
        && targetRect.left   >= viewerRect.left
        && targetRect.right  <= viewerRect.right;

      if (!fullyVisible) {
        console.log(`[RectNavigator] Same-PDF jump to id=${id}, page=${page + 1}, rect not fully visible`);
        const fitScale = viewer.getPageFitScale(page + 1);
        if (fitScale !== null) {
          console.log(`[RectNavigator] Applying fit scale: ${fitScale}`);
          applyZoom(fitScale);
        }
        pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });
      } else {
        console.log(`[RectNavigator] Same-PDF jump to id=${id}, page=${page + 1}, rect fully visible, no zoom needed`);
      }
    }
  }

  // Update page controller to reflect navigation
  onNavigateToPage?.(page + 1);
}
