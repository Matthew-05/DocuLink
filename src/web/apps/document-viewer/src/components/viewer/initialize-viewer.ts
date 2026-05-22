import { createToolbar } from "../toolbar/toolbar.js";
import { ZoomController } from "../toolbar/zoom-controller.js";
import { connectViewerToHostBridge } from "./viewer-bridge.js";
import { RectDrawOverlay } from "./rect-draw-overlay.js";
import { RectRenderer } from "./rect-renderer.js";
import { RectContextMenu } from "./rect-context-menu.js";
import { CharBboxOverlay } from "./char-bbox-overlay.js";
import { createRectNavigator } from "./rect-navigator.js";
import { TextContentCache } from "../../services/text-content-cache.js";
import {
  sendLinkRectangleCreated,
  sendLinkRectangleClicked,
  sendLinkRectangleDeleted,
  sendCacheBuildStarted,
  sendCacheBuildComplete,
} from "../../host-bridge.js";
import type { PdfViewer } from "./pdf-viewer.js";

interface DocuLinkDebugApi {
  toggleCharBboxes: () => boolean;
  showCharBboxes: () => void;
  hideCharBboxes: () => void;
}

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

  const cache         = new TextContentCache();
  const renderer      = new RectRenderer(viewer);
  const contextMenu   = new RectContextMenu();
  const overlay       = new RectDrawOverlay(viewer, cache);
  const charBboxDebug = new CharBboxOverlay(viewer, cache);

  contextMenu.attachScrollTarget(viewer.element);

  overlay.onRectCreated((payload) => {
    sendLinkRectangleCreated(payload);
    renderer.addRectangle({
      id:    `temp-${Date.now()}`,
      pdfId: payload.pdfId,
      page:  payload.page,
      rect:  payload.rect,
    });
  });

  renderer.onRectClicked((id) => sendLinkRectangleClicked(id));

  renderer.onRectContextMenu((id, x, y) => {
    contextMenu.show(x, y, id);
  });

  contextMenu.onDelete((id) => {
    sendLinkRectangleDeleted(id);
  });

  let cacheGeneration = 0;

  viewer.onDocumentChanged(() => {
    const pdfId = viewer.getActivePdfId();
    const doc   = viewer.getDocument();
    if (!pdfId || !doc) return;

    const gen = ++cacheGeneration;
    cache.clear();
    sendCacheBuildStarted();
    void cache.buildAll(pdfId, doc)
      .then(() => {
        if (gen !== cacheGeneration) return;
        charBboxDebug.refresh();
        sendCacheBuildComplete();
      })
      .catch(() => { if (gen === cacheGeneration) sendCacheBuildComplete(); });
  });

  // ── Console debug API ─────────────────────────────────────────────────────

  (window as Window & { __docuLink?: DocuLinkDebugApi }).__docuLink = {
    toggleCharBboxes: () => charBboxDebug.toggle(),
    showCharBboxes:   () => charBboxDebug.show(),
    hideCharBboxes:   () => charBboxDebug.hide(),
  };

  // ── Host bridge ───────────────────────────────────────────────────────────

  const navigate = createRectNavigator(viewer, selector, renderer);

  connectViewerToHostBridge(
    viewer,
    selector,
    (rects) => {
      contextMenu.hide();
      renderer.setRectangles(rects);
    },
    navigate,
    () => { renderer.clearHighlight(); },
    (ids) => {
      contextMenu.hide();
      renderer.removeRectangles(ids);
    },
  );

  return { toolbarElement };
}
