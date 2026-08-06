import { initHostBridge, sendViewerContentReady } from "../../host-bridge.js";
import type { HostMessageHandlers } from "../../host-bridge.js";
import type { TextContentCache } from "../../services/text-content-cache.js";
import type { FolderEntry, PdfEntry } from "../../types/index.js";
import type { FolderFilter } from "../toolbar/folder-filter.js";
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
/**
 * Handlers supplied by the caller. PDF lifecycle messages (`onPdfsLoaded`,
 * `onPdfUpdated`, `onPdfNameUpdated`, `onPdfRemoved`) are owned by this module
 * because they drive the selector, cache, and document loading.
 */
export type ViewerHostHandlers = Omit<
  HostMessageHandlers,
  | "onPdfsLoaded"
  | "onPdfUpdated"
  | "onPdfNameUpdated"
  | "onPdfRemoved"
  | "onShowPdf"
  | "onFoldersUpdated"
>;

export function connectViewerToHostBridge(
  viewer: PdfViewer,
  selector: PdfSelector,
  folderFilter: FolderFilter,
  cache: TextContentCache,
  onIndexingStateChange: (indexing: boolean) => void,
  handlers: ViewerHostHandlers = {},
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

  const reloadEntry = async (entry: PdfEntry): Promise<void> => {
    selector.setActiveId(entry.id);
    await viewer.loadDocument(entry.url, entry.id, entry.pageRotations);
    await viewer.renderPageNow(1);
    viewer.startBackgroundRender();
  };

  initHostBridge({
    ...handlers,

    onFoldersUpdated: (folders, assignments) => {
      selector.updateFolderAssignments(assignments);
      folderFilter.setFolders(folders);
    },

    onPdfsLoaded: (entries, folders) => {
      void (async () => {
        try {
          selector.setEntries(entries);
          folderFilter.setFolders(folders);

          startIndexing();
          void indexAllPdfs(cache, entries)
            .finally(endIndexing);

          const target = pickEntryToLoad(entries, viewer.getActivePdfId());
          if (target) {
            await reloadEntry(target);
          } else {
            viewer.showNoPdfsState();
          }
        } finally {
          sendViewerContentReady();
        }
      })();
    },

    onPdfUpdated: (entry) => {
      selector.upsertEntry(entry);

      startIndexing();
      void (async () => {
        cache.clearPdf(entry.id);
        await cache.buildForUrl(entry.id, entry.url, entry.geometryBase64);
      })().finally(endIndexing);

      if (viewer.getActivePdfId() === entry.id) {
        void reloadEntry(entry);
      }
    },

    onPdfNameUpdated: (id, name) => {
      selector.updateEntryName(id, name);
    },

    // The user picked a document in the file manager. Page, zoom and highlight
    // state are left alone — this is a document swap, not a navigation.
    onShowPdf: (pdfId) => {
      if (viewer.getActivePdfId() === pdfId) return;
      const entry = selector.getEntry(pdfId);
      if (!entry) return;
      void reloadEntry(entry);
    },

    onPdfRemoved: (id) => {
      cache.clearPdf(id);
      selector.removeEntry(id);
      if (viewer.getActivePdfId() === id) {
        const next = selector.getEntries()[0];
        if (next) void reloadEntry(next);
        else viewer.showNoPdfsState();
      }
    },
  });
}
