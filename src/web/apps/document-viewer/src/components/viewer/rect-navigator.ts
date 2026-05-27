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

// Helper to get fit scale, waiting for viewer dimensions to stabilize if needed
async function getFitScaleWhenReady(viewer: PdfViewer, pageNumber: number): Promise<ZoomLevel> {
  let lastW = 0;
  let lastH = 0;
  let stableCount = 0;
  const stabilityThreshold = 2; // dimensions must match for 2 frames

  for (let attempts = 0; attempts < 30; attempts++) {
    const scale = viewer.getPageFitScale(pageNumber);
    if (scale !== null) {
      const w = viewer.element.clientWidth;
      const h = viewer.element.clientHeight;

      // Check if dimensions have stabilized
      if (w === lastW && h === lastH) {
        stableCount++;
        if (stableCount >= stabilityThreshold) {
          console.log(`[RectNavigator] Fit scale ready: ${scale} (dimensions stable at ${w}×${h})`);
          return scale;
        }
      } else {
        // Dimensions changed; reset stability counter
        stableCount = 0;
        lastW = w;
        lastH = h;
        console.log(`[RectNavigator] Layout settling: dimensions now ${w}×${h}`);
      }
    }
    // Wait for layout engine to run
    await new Promise<void>(resolve => {
      requestAnimationFrame(() => resolve());
    });
  }
  // Timeout; return best guess
  const scale = viewer.getPageFitScale(pageNumber);
  console.log(`[RectNavigator] Fit scale timeout, using ${scale ?? 1.0}`);
  return scale ?? 1.0;
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

  // Unified navigation logic (same-PDF and cross-PDF converge here)
  // Order is critical: zoom → scroll → render → background
  console.log(`[RectNavigator] Navigating to id=${id}, page=${page + 1}, crossPdf=${crossPdf}`);

  // For same-PDF, check if zoom is needed. For cross-PDF, always zoom to fit.
  if (crossPdf) {
    console.log(`[RectNavigator] Cross-PDF: getting fit scale`);
    const fitScale = await getFitScaleWhenReady(viewer, page + 1);
    applyZoom(fitScale);
  } else {
    // Same-PDF: apply fit zoom if page is unrendered or we're at default zoom level
    const pageCanvas = pageWrapper.querySelector('canvas');
    const isPageUnrendered = !pageCanvas;
    const isAtDefaultZoom = viewer.getCurrentZoom() === 1.0;

    if (isPageUnrendered || isAtDefaultZoom) {
      console.log(`[RectNavigator] Same-PDF: getting fit scale (unrendered=${isPageUnrendered}, defaultZoom=${isAtDefaultZoom})`);
      const fitScale = await getFitScaleWhenReady(viewer, page + 1);
      applyZoom(fitScale);
    } else {
      // Page is rendered and we've already zoomed; check if rect is fully visible
      const viewerRect = viewer.element.getBoundingClientRect();
      const targetRect = rectEl.getBoundingClientRect();
      const fullyVisible = targetRect.top    >= viewerRect.top
        && targetRect.bottom <= viewerRect.bottom
        && targetRect.left   >= viewerRect.left
        && targetRect.right  <= viewerRect.right;

      if (!fullyVisible) {
        console.log(`[RectNavigator] Same-PDF: rect not fully visible, getting fit scale`);
        const fitScale = await getFitScaleWhenReady(viewer, page + 1);
        applyZoom(fitScale);
      } else {
        console.log(`[RectNavigator] Same-PDF: rect fully visible at current zoom, no zoom needed`);
      }
    }
  }

  // Step 2: Scroll viewport to target page (still showing white placeholder)
  console.log(`[RectNavigator] Scrolling to page ${page + 1}`);
  pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });

  // Step 3: Render target page (user watches it fill in)
  console.log(`[RectNavigator] Rendering target page ${page + 1}`);
  await viewer.renderPageNow(page + 1);
  console.log(`[RectNavigator] Target page rendered, starting background render`);

  // Step 4: Fill in all other pages in document order
  viewer.startBackgroundRender();

  // Update page controller to reflect navigation
  onNavigateToPage?.(page + 1);
}
