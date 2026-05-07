import { createToolbar } from "../toolbar/toolbar.js";
import { ZoomController } from "../toolbar/zoom-controller.js";
import { connectViewerToHostBridge } from "./viewer-bridge.js";
import { RectDrawOverlay } from "./rect-draw-overlay.js";
import { RectRenderer } from "./rect-renderer.js";
import { TextContentCache } from "../../services/text-content-cache.js";
import {
  sendLinkRectangleCreated,
  sendCacheBuildStarted,
  sendCacheBuildComplete,
} from "../../host-bridge.js";
import type { PdfViewer } from "./pdf-viewer.js";

/**
 * Creates and wires the toolbar, rect-draw overlay, and text-content cache
 * to the viewer, then connects the host bridge.
 * Returns the toolbar element for the caller to mount in the DOM.
 */
export function initializeViewer(viewer: PdfViewer): { toolbarElement: HTMLElement } {
  const { element: toolbarElement, zoom, page, selector } = createToolbar();

  viewer.onLoaded((total) => {
    page.setTotal(total);
    page.setCurrentPage(1);
  });

  zoom.onChange((scale) => {
    viewer.setZoom(scale);
  });

  page.onChange((pageNum) => {
    viewer.scrollToPage(pageNum);
  });

  selector.onSelect((entry) => {
    void viewer.loadDocument(entry.url, entry.id);
  });

  viewer.element.addEventListener(
    "wheel",
    (e) => {
      if (!e.ctrlKey) return;
      e.preventDefault();
      zoom.adjustBy(e.deltaY > 0 ? -ZoomController.SCROLL_STEP : ZoomController.SCROLL_STEP);
    },
    { passive: false }
  );

  // ── Text cache & rect-draw overlay ────────────────────────────────────────

  const cache    = new TextContentCache();
  const renderer = new RectRenderer(viewer);
  const overlay  = new RectDrawOverlay(viewer, cache);

  overlay.onRectCreated((payload) => {
    sendLinkRectangleCreated(payload);
    renderer.addRectangle({
      id:    `temp-${Date.now()}`,
      pdfId: payload.pdfId,
      page:  payload.page,
      rect:  payload.rect,
    });
  });

  viewer.onDocumentChanged(() => {
    const pdfId = viewer.getActivePdfId();
    const doc   = viewer.getDocument();
    if (!pdfId || !doc) return;

    cache.clear();
    sendCacheBuildStarted();
    void cache.buildAll(pdfId, doc).then(() => {
      sendCacheBuildComplete();
    });
  });

  // ── Host bridge ───────────────────────────────────────────────────────────

  connectViewerToHostBridge(viewer, selector, (rects) => {
    renderer.setRectangles(rects);
  });

  return { toolbarElement };
}
