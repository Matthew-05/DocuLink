import { initHostBridge } from "../../host-bridge.js";
import type { LinkedRectEntry } from "../../types/index.js";
import type { PdfSelector } from "../toolbar/pdf-selector.js";
import type { PdfViewer } from "./pdf-viewer.js";

/**
 * Wires the WebView2 host bridge to the viewer and selector.
 *
 * On each `pdfs-loaded` message the selector entries are refreshed and the
 * first PDF is loaded automatically so the viewer is never left blank when
 * documents are available.
 */
export function connectViewerToHostBridge(
  viewer: PdfViewer,
  selector: PdfSelector,
  onLinkedRectangles?: (rects: LinkedRectEntry[]) => void,
): void {
  initHostBridge((entries) => {
    selector.setEntries(entries);

    const first = entries[0];
    if (first) {
      selector.setActiveId(first.id);
      void viewer.loadDocument(first.url, first.id);
    }
  }, onLinkedRectangles);
}
