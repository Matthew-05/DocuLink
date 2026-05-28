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
          return scale;
        }
      } else {
        // Dimensions changed; reset stability counter
        stableCount = 0;
        lastW = w;
        lastH = h;
      }
    }
    // Wait for layout engine to run
    await new Promise<void>(resolve => {
      requestAnimationFrame(() => resolve());
    });
  }
  // Timeout; return best guess
  const scale = viewer.getPageFitScale(pageNumber);
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
  const crossPdf = viewer.getActivePdfId() !== pdfId;

  if (crossPdf) {
    const entry = selector.getEntry(pdfId);
    if (!entry) return;
    selector.setActiveId(pdfId);
    await viewer.loadDocument(entry.url, pdfId);

    // Snap to target page immediately so user sees correct placeholder, not page 1,
    // during the getFitScaleWhenReady polling loop.
    const earlyPageWrapper = viewer.element.querySelector<HTMLDivElement>(
      `[data-page="${page + 1}"]`,
    );
    earlyPageWrapper?.scrollIntoView({ behavior: "instant" as ScrollBehavior });
  } else {
    await viewer.waitForLoad();
  }

  if (!renderer.hasRectangle(id)) return;

  const rectEl = viewer.element.querySelector<HTMLElement>(
    `[data-rect-id="${CSS.escape(id)}"]`,
  );
  if (!rectEl) return;

  renderer.highlightRectangle(id);

  const pageWrapper = viewer.element.querySelector<HTMLDivElement>(
    `[data-page="${page + 1}"]`,
  );
  if (!pageWrapper) return;

  // Unified navigation logic (same-PDF and cross-PDF converge here)
  // Order is critical: zoom → scroll → render → background
  if (crossPdf) {
    const fitScale = await getFitScaleWhenReady(viewer, page + 1);
    applyZoom(fitScale);
  } else {
    // Same-PDF: apply fit zoom if page is unrendered or we're at default zoom level
    const pageCanvas = pageWrapper.querySelector('canvas');
    const isPageUnrendered = !pageCanvas;
    const isAtDefaultZoom = viewer.getCurrentZoom() === 1.0;

    if (isPageUnrendered || isAtDefaultZoom) {
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
        const fitScale = await getFitScaleWhenReady(viewer, page + 1);
        applyZoom(fitScale);
      }
    }
  }

  // Step 2: Scroll viewport to target page (still showing white placeholder)
  pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });

  // Step 3: Render target page (user watches it fill in)
  await viewer.renderPageNow(page + 1);

  // Step 4: Fill in all other pages in document order
  viewer.startBackgroundRender();

  // Update page controller to reflect navigation
  onNavigateToPage?.(page + 1);
}
