import { createToolbar } from "../toolbar/toolbar.js";
import { ZoomController } from "../toolbar/zoom-controller.js";
import { LinkTypeSelector } from "../toolbar/link-type-selector.js";
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
  sendRotatePage,
} from "../../host-bridge.js";
import type { SearchMatch, LinkedRectEntry } from "../../types/index.js";
import type { PdfViewer } from "./pdf-viewer.js";

interface DocuLinkDebugApi {
  toggleCharBboxes: () => boolean;
  showCharBboxes: () => void;
  hideCharBboxes: () => void;
}

/**
 * Creates and wires the toolbar, rect-draw overlay, floating link-type bar,
 * and text-content cache to the viewer, then connects the host bridge.
 * Returns the toolbar element and a viewer wrapper (viewer + floating bar)
 * for the caller to mount in the DOM.
 */
export function initializeViewer(viewer: PdfViewer): { toolbarElement: HTMLElement; viewerWrapper: HTMLElement } {
  const { element: toolbarElement, zoom, page, selector, search, rotate } = createToolbar();

  const linkTypeSelector = new LinkTypeSelector();

  viewer.onLoaded((total) => {
    page.setTotal(total);
    page.setCurrentPage(1);
  });

  let currentPage = 1;

  zoom.onChange((scale, anchor) => {
    viewer.setZoom(scale, anchor);
  });

  viewer.onLoaded(() => {
    currentPage = 1;
  });

  page.onChange((pageNum) => {
    currentPage = pageNum;
    viewer.scrollToPage(pageNum);
  });

  zoom.onFitPage(() => {
    const fitScale = viewer.getPageFitScale(currentPage);
    if (fitScale === null) return;
    zoom.setScale(fitScale);
    viewer.setZoom(fitScale);
  });

  selector.onSelect((entry) => {
    void viewer.loadDocument(entry.url, entry.id, entry.pageRotations).then(() => viewer.startBackgroundRender());
  });

  rotate.onRotateCcw(() => {
    const pdfId = viewer.getActivePdfId();
    if (!pdfId) return;
    sendRotatePage(pdfId, currentPage - 1, "ccw");
  });

  rotate.onRotateCw(() => {
    const pdfId = viewer.getActivePdfId();
    if (!pdfId) return;
    sendRotatePage(pdfId, currentPage - 1, "cw");
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

  const onNavigateToPage = (pageNumber: number): void => {
    currentPage = pageNumber;
    page.setCurrentPage(pageNumber);
  };

  const updatePageFromScroll = (): void => {
    const layout = viewer.getPageLayout();
    if (layout.length === 0) return;

    const viewerRect = viewer.element.getBoundingClientRect();
    let mostVisiblePage = layout[0]!.pageNumber;
    let maxVisibleHeight = 0;

    for (const { pageNumber, wrapper } of layout) {
      const wrapperRect = wrapper.getBoundingClientRect();
      const visibleTop = Math.max(wrapperRect.top, viewerRect.top);
      const visibleBottom = Math.min(wrapperRect.bottom, viewerRect.bottom);
      const visibleHeight = Math.max(0, visibleBottom - visibleTop);

      if (visibleHeight > maxVisibleHeight) {
        maxVisibleHeight = visibleHeight;
        mostVisiblePage = pageNumber;
      }
    }

    if (mostVisiblePage !== currentPage) {
      onNavigateToPage(mostVisiblePage);
    }
  };

  viewer.element.addEventListener("scroll", updatePageFromScroll, { passive: true });

  // ── Text cache & rect-draw overlay ────────────────────────────────────────

  let _currentRects: LinkedRectEntry[] = [];

  const computeLinkCounts = (rects: LinkedRectEntry[]): Record<string, number> => {
    const counts: Record<string, number> = {};
    for (const r of rects) counts[r.pdfId] = (counts[r.pdfId] ?? 0) + 1;
    return counts;
  };

  const cache           = new TextContentCache();
  const renderer        = new RectRenderer(viewer);
  const contextMenu     = new RectContextMenu();
  const overlay         = new RectDrawOverlay(viewer, cache);
  const editOverlay     = new RectEditOverlay(viewer, cache, renderer);
  const charBboxDebug   = new CharBboxOverlay(viewer, cache);
  const matchRenderer   = new SearchMatchRenderer(viewer);
  const searcher        = new PdfTextSearcher(cache);
  const searchNavigator = createSearchNavigator(viewer, selector, matchRenderer, (scale) => {
    zoom.setScale(scale);
    viewer.setZoom(scale);
  }, onNavigateToPage);

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

  document.addEventListener(
    "keydown",
    (e) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || e.key.toLowerCase() !== "f") return;

      e.preventDefault();
      e.stopPropagation();
      search.focus();
    },
    true
  );

  search.disable();

  contextMenu.attachScrollTarget(viewer.element);

  overlay.onRectCreated((payload) => {
    const linkType = linkTypeSelector.getLinkType();
    sendLinkRectangleCreated({ ...payload, linkType });
    renderer.addRectangle({
      id:    `temp-${Date.now()}`,
      pdfId: payload.pdfId,
      page:  payload.page,
      rect:  payload.rect,
      linkType,
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
  }, onNavigateToPage);

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
      _currentRects = rects;
      renderer.setRectangles(rects);
      selector.updateLinkCounts(computeLinkCounts(rects));
    },
    navigate,
    () => { renderer.clearHighlight(); },
    (id) => { renderer.highlightRectangle(id); },
    (ids) => {
      contextMenu.hide();
      renderer.removeRectangles(ids);
      const removed = new Set(ids);
      _currentRects = _currentRects.filter((r) => !removed.has(r.id));
      selector.updateLinkCounts(computeLinkCounts(_currentRects));
    },
    (pdfId, rotations) => {
      selector.updatePdfRotations(pdfId, rotations);
      if (pdfId === viewer.getActivePdfId()) {
        for (const [k, v] of Object.entries(rotations)) {
          viewer.setPageRotation(Number(k), v);
        }
      }
    },
  );

  // ── Floating link-type bar ────────────────────────────────────────────────

  const linkTypeBar = document.createElement("div");
  linkTypeBar.className = "link-type-bar";

  linkTypeBar.append(linkTypeSelector.element);

  const viewerWrapper = document.createElement("div");
  viewerWrapper.className = "viewer-wrapper";
  viewerWrapper.append(viewer.element, linkTypeBar);

  return { toolbarElement, viewerWrapper };
}
