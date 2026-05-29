import { initHostBridge } from "../../host-bridge.js";
import type { TextContentCache } from "../../services/text-content-cache.js";
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

async function indexAllPdfs(
  cache: TextContentCache,
  entries: PdfEntry[],
): Promise<void> {
  cache.clear();
  await Promise.all(
    entries.map((entry) => cache.buildForUrl(entry.id, entry.url, entry.geometryBase64)),
  );
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
  cache: TextContentCache,
  onIndexingStateChange: (indexing: boolean) => void,
  onLinkedRectangles?: (rects: LinkedRectEntry[]) => void,
  onNavigateToRectangle?: (id: string, pdfId: string, page: number) => void,
  onClearRectangleHighlight?: () => void,
  onHighlightRectangle?: (id: string) => void,
  onLinkRectanglesRemoved?: (ids: string[]) => void,
  onPageRotationsUpdated?: (pdfId: string, rotations: Record<number, number>) => void,
): void {
  let indexingCount = 0;

  const startIndexing = (): void => {
    indexingCount++;
    if (indexingCount === 1) onIndexingStateChange(true);
  };

  const endIndexing = (): void => {
    indexingCount = Math.max(0, indexingCount - 1);
    if (indexingCount === 0) onIndexingStateChange(false);
  };

  const reloadEntry = (entry: PdfEntry): void => {
    selector.setActiveId(entry.id);
    void viewer.loadDocument(entry.url, entry.id, entry.pageRotations).then(() => viewer.startBackgroundRender());
  };

  initHostBridge(
    (entries) => {
      selector.setEntries(entries);

      startIndexing();
      void indexAllPdfs(cache, entries)
        .finally(endIndexing);

      const target = pickEntryToLoad(entries, viewer.getActivePdfId());
      if (target) {
        reloadEntry(target);
      } else {
        viewer.showNoPdfsState();
      }
    },
    onLinkedRectangles,
    onNavigateToRectangle,
    onClearRectangleHighlight,
    onHighlightRectangle,
    (entry) => {
      selector.upsertEntry(entry);

      startIndexing();
      void (async () => {
        cache.clearPdf(entry.id);
        await cache.buildForUrl(entry.id, entry.url, entry.geometryBase64);
      })().finally(endIndexing);

      if (viewer.getActivePdfId() === entry.id) {
        reloadEntry(entry);
      }
    },
    onLinkRectanglesRemoved,
    (id, name) => {
      selector.updateEntryName(id, name);
    },
    (id) => {
      cache.clearPdf(id);
      selector.removeEntry(id);
      if (viewer.getActivePdfId() === id) {
        const next = selector.getEntries()[0];
        if (next) reloadEntry(next);
        else viewer.showNoPdfsState();
      }
    },
    onPageRotationsUpdated,
  );
}
