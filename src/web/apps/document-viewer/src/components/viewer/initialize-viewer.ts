import { createToolbar } from "../toolbar/toolbar.js";
import { ZoomController } from "../toolbar/zoom-controller.js";
import { connectViewerToHostBridge } from "./viewer-bridge.js";
import type { PdfViewer } from "./pdf-viewer.js";

/**
 * Creates and wires the toolbar to the viewer, then connects the host bridge.
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
    void viewer.loadDocument(entry.url);
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

  connectViewerToHostBridge(viewer, selector);

  return { toolbarElement };
}
