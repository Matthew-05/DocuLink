import { createToolbar } from "../toolbar/toolbar.js";
import { ZoomController } from "../toolbar/zoom-controller.js";
import { connectViewerToHostBridge } from "./viewer-bridge.js";
import { RectDrawOverlay } from "./rect-draw-overlay.js";
import { RectEditOverlay } from "./rect-edit-overlay.js";
import { RectRenderer } from "./rect-renderer.js";
import { RectContextMenu } from "./rect-context-menu.js";
import { CharBboxOverlay } from "./char-bbox-overlay.js";
import { createRectNavigator } from "./rect-navigator.js";
import { PdfTextSearcher, normalizeSearchQuery } from "./pdf-text-searcher.js";
import { SearchMatchRenderer } from "./search-match-renderer.js";
import { createSearchNavigator } from "./search-navigator.js";
import { TextContentCache } from "../../services/text-content-cache.js";
import {
  sendLinkRectangleCreated,
  sendLinkRectangleUpdated,
  sendLinkRectangleClicked,
  sendLinkRectangleDeleted,
  sendCacheBuildStarted,
  sendCacheBuildComplete,
} from "../../host-bridge.js";
import type { SearchMatch } from "../../types/index.js";
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
  const { element: toolbarElement, zoom, page, selector, search } = createToolbar();

  viewer.onLoaded((total) => {
    page.setTotal(total);
    page.setCurrentPage(1);
  });

  zoom.onChange((scale, anchor) => {
    viewer.setZoom(scale, anchor);
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
      const rect = viewer.element.getBoundingClientRect();
      const anchor = { x: e.clientX - rect.left, y: e.clientY - rect.top };
      zoom.adjustBy(
        e.deltaY > 0 ? -ZoomController.SCROLL_STEP : ZoomController.SCROLL_STEP,
        anchor
      );
    },
    { passive: false }
  );

  // ── Text cache & rect-draw overlay ────────────────────────────────────────

  const cache           = new TextContentCache();
  const renderer        = new RectRenderer(viewer);
  const contextMenu     = new RectContextMenu();
  const overlay         = new RectDrawOverlay(viewer, cache);
  const editOverlay     = new RectEditOverlay(viewer, cache, renderer);
  const charBboxDebug   = new CharBboxOverlay(viewer, cache);
  const matchRenderer   = new SearchMatchRenderer(viewer);
  const searcher        = new PdfTextSearcher(cache);
  const searchNavigator = createSearchNavigator(viewer, selector, matchRenderer);

  let lastSearchResults: SearchMatch[] = [];
  let focusedMatch: SearchMatch | null = null;

  const applyActivePdfHighlights = (): void => {
    const activePdfId = viewer.getActivePdfId();
    if (!activePdfId) {
      matchRenderer.clearMatches();
      return;
    }

    if (focusedMatch && focusedMatch.pdfId === activePdfId) {
      matchRenderer.setMatches([focusedMatch]);
      matchRenderer.highlightMatch(focusedMatch.id);
      return;
    }

    if (focusedMatch && focusedMatch.pdfId !== activePdfId) {
      focusedMatch = null;
    }

    matchRenderer.setMatches(lastSearchResults.filter((m) => m.pdfId === activePdfId));
  };

  const runSearch = (query: string): void => {
    focusedMatch = null;

    if (!query) {
      search.clearResults();
      matchRenderer.clearMatches();
      lastSearchResults = [];
      return;
    }

    lastSearchResults = searcher.search(query, selector.getEntries());
    search.setResults(lastSearchResults);
    applyActivePdfHighlights();
  };

  search.onQuery(runSearch);

  search.onMatchClicked((match) => {
    focusedMatch = match;
    searchNavigator(match, lastSearchResults);
  });

  search.disable();

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

  editOverlay.onRectUpdated((payload) => {
    sendLinkRectangleUpdated(payload);
  });

  renderer.setClickGuard(() => editOverlay.consumeClickSuppression());

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

    const finish = (): void => {
      charBboxDebug.refresh();
      const query = normalizeSearchQuery(search.getQuery());
      if (query) {
        applyActivePdfHighlights();
      }
    };

    if (cache.has(pdfId)) {
      finish();
      return;
    }

    const gen = ++cacheGeneration;
    sendCacheBuildStarted();

    const entry = selector.getEntry(pdfId);
    const buildPromise = entry
      ? cache.buildForUrl(pdfId, entry.url, entry.geometryBase64)
      : cache.buildFromDoc(pdfId, doc);

    void buildPromise
      .then(() => {
        if (gen !== cacheGeneration) return;
        finish();
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

  const navigate = createRectNavigator(viewer, selector, renderer, (scale) => {
    zoom.setScale(scale);
    viewer.setZoom(scale);
  });

  connectViewerToHostBridge(
    viewer,
    selector,
    cache,
    (indexing) => {
      if (indexing) {
        search.disable();
      } else {
        search.enable();
        const query = normalizeSearchQuery(search.getQuery());
        if (query) runSearch(query);
      }
    },
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
