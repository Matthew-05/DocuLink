import { initHostBridge } from "../../host-bridge.js";
import type { PdfEntry, LinkedRectEntry } from "../../types/index.js";
import type { PdfSelector } from "../toolbar/pdf-selector.js";
import type { PdfViewer } from "./pdf-viewer.js";

/** Pick the PDF to display after the host pushes an updated list. */
function pickEntryToLoad(entries: PdfEntry[], activeId: string | null): PdfEntry | undefined {
  if (entries.length === 0) return undefined;
  if (activeId) {
    return entries.find((entry) => entry.id === activeId) ?? entries[0];
  }
  return entries[0];
}

/**
 * Wires the WebView2 host bridge to the viewer and selector.
 *
 * On each `pdfs-loaded` message the selector entries are refreshed and the
 * active PDF is reloaded (falling back to the first entry when none is active)
 * so OCR updates and other storage changes are reflected in the viewer.
 */
export function connectViewerToHostBridge(
  viewer: PdfViewer,
  selector: PdfSelector,
  onLinkedRectangles?: (rects: LinkedRectEntry[]) => void,
  onNavigateToRectangle?: (id: string, pdfId: string, page: number) => void,
  onClearRectangleHighlight?: () => void,
  onLinkRectanglesRemoved?: (ids: string[]) => void,
): void {
  const reloadEntry = (entry: PdfEntry): void => {
    selector.setActiveId(entry.id);
    void viewer.loadDocument(entry.url, entry.id);
  };

  initHostBridge(
    (entries) => {
      selector.setEntries(entries);

      const target = pickEntryToLoad(entries, viewer.getActivePdfId());
      if (target) {
        reloadEntry(target);
      }
    },
    onLinkedRectangles,
    onNavigateToRectangle,
    onClearRectangleHighlight,
    (entry) => {
      selector.upsertEntry(entry);

      if (viewer.getActivePdfId() === entry.id) {
        reloadEntry(entry);
      }
    },
    onLinkRectanglesRemoved,
  );
}
