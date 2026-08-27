import { createToolbar } from "../toolbar/toolbar.js";
import { ZoomController } from "../toolbar/zoom-controller.js";
import { LinkTypeSelector } from "../toolbar/link-type-selector.js";
import { connectViewerToHostBridge } from "./viewer-bridge.js";
import { RectDrawOverlay } from "./rect-draw-overlay.js";
import { RectEditOverlay } from "./rect-edit-overlay.js";
import { RectRenderer } from "./rect-renderer.js";
import { TableGridEditor } from "./table-grid-editor.js";
import { RectContextMenu } from "./rect-context-menu.js";
import { LinkSelectionPanel } from "./link-selection-panel.js";
import { CharBboxOverlay } from "./char-bbox-overlay.js";
import { createRectNavigator } from "./rect-navigator.js";
import { attachExcelKeyBridge } from "./excel-key-bridge.js";
import { createFitMode } from "./fit-mode.js";
import { measureVisiblePages } from "./page-visibility.js";
import { PdfTextSearcher } from "./pdf-text-searcher.js";
import { SearchMatchRenderer } from "./search-match-renderer.js";
import { createSearchNavigator } from "./search-navigator.js";
import { TableCopyModal } from "../table-copy-modal/table-copy-modal.js";
import { TextContentCache } from "../../services/text-content-cache.js";
import {
  detectCopiedTable,
  detectTableGrid,
} from "../../services/table-extractor.js";
import {
  sendCopyTableSelection,
  sendLinkRectangleCreated,
  sendLinkRectangleUpdated,
  sendLinkRectangleClicked,
  sendLinkRectangleDeleted,
  sendCacheBuildStarted,
  sendCacheBuildComplete,
  sendRotatePage,
} from "../../host-bridge.js";
import type { SearchMatch, LinkedRectEntry, LinkSelectionEntry, ZoomLevel } from "../../types/index.js";
import type { PdfEntry } from "../../types/index.js";
import type { PdfViewer } from "./pdf-viewer.js";

const INITIAL_SEARCH_RESULT_LIMIT = 150;
const SEARCH_RESULT_BATCH_SIZE = 50;
const SEARCH_PAGE_BATCH_SIZE = 12;
const SEARCH_RESULTS_PANEL_LIMIT = 500;
const SEARCH_BATCH_DELAY_MS = 20;

function prioritizePdfEntries(entries: PdfEntry[], preferredPdfId: string | null): PdfEntry[] {
  if (!preferredPdfId) return entries;

  const preferred = entries.find((entry) => entry.id === preferredPdfId);
  if (!preferred) return entries;

  return [
    preferred,
    ...entries.filter((entry) => entry.id !== preferredPdfId),
  ];
}

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
  const { element: toolbarElement, zoom, page, folderFilter, selector, search, rotate } =
    createToolbar();

  const linkTypeSelector = new LinkTypeSelector();

  viewer.onLoaded((total) => {
    page.setTotal(total);
    page.setCurrentPage(1);
  });

  let currentPage = 1;
  let onVisiblePageChanged = (): void => {};

  const fitMode = createFitMode(
    viewer,
    (scale) => {
      zoom.setScale(scale);
      viewer.setZoom(scale);
    },
    () => currentPage,
  );

  /** Applies a page-fit scale and keeps it fitted across later viewer resizes. */
  const applyFitZoom = (scale: ZoomLevel, pageNumber?: number): void => {
    zoom.setScale(scale);
    viewer.setZoom(scale);
    fitMode.enter(pageNumber);
  };

  // Only the +/- buttons and ctrl+wheel reach this callback, so it means the user
  // has chosen an explicit zoom level and no longer wants the page kept fitted.
  zoom.onChange((scale, anchor) => {
    fitMode.exit();
    viewer.setZoom(scale, anchor);
  });

  viewer.onLoaded(() => {
    currentPage = 1;
  });

  page.onChange((pageNum) => {
    currentPage = pageNum;
    viewer.scrollToPage(pageNum);
    onVisiblePageChanged();
  });

  zoom.onFitPage(() => {
    const fitScale = viewer.getPageFitScale(currentPage);
    if (fitScale === null) return;
    applyFitZoom(fitScale);
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
    fitMode.releasePin();
    page.setCurrentPage(pageNumber);
    onVisiblePageChanged();
  };

  const getVisiblePageMeasurements = (): Array<{ pageNumber: number; visibleHeight: number }> => {
    const viewerRect = viewer.element.getBoundingClientRect();
    const pageBounds = viewer.getPageLayout().map(({ pageNumber, wrapper }) => {
      const wrapperRect = wrapper.getBoundingClientRect();
      return { pageNumber, top: wrapperRect.top, bottom: wrapperRect.bottom };
    });
    return measureVisiblePages(viewerRect, pageBounds);
  };

  const getVisiblePageIndices = (): number[] => {
    const visiblePages = getVisiblePageMeasurements();
    return visiblePages.length > 0
      ? visiblePages.map(({ pageNumber }) => pageNumber - 1)
      : [currentPage - 1];
  };

  let visiblePageSetKey = "";

  const updatePageFromScroll = (): void => {
    const visiblePages = getVisiblePageMeasurements();
    if (visiblePages.length === 0) return;

    let mostVisiblePage = visiblePages[0]!.pageNumber;
    let maxVisibleHeight = visiblePages[0]!.visibleHeight;
    for (const { pageNumber, visibleHeight } of visiblePages.slice(1)) {
      if (visibleHeight <= maxVisibleHeight) continue;
      maxVisibleHeight = visibleHeight;
      mostVisiblePage = pageNumber;
    }

    const nextVisiblePageSetKey = visiblePages.map(({ pageNumber }) => pageNumber).join(",");
    const visiblePageSetChanged = nextVisiblePageSetKey !== visiblePageSetKey;
    visiblePageSetKey = nextVisiblePageSetKey;

    if (mostVisiblePage !== currentPage) {
      onNavigateToPage(mostVisiblePage);
    } else if (visiblePageSetChanged) {
      onVisiblePageChanged();
    }
  };

  viewer.element.addEventListener("scroll", updatePageFromScroll, { passive: true });

  viewer.element.addEventListener("mousedown", () => {
    search.blur();
    search.hideResults();
  }, { passive: true });

  // ── Text cache & rect-draw overlay ────────────────────────────────────────

  let _currentRects: LinkedRectEntry[] = [];
  let _currentSelection: LinkSelectionEntry[] = [];

  const computeLinkCounts = (rects: LinkedRectEntry[]): Record<string, number> => {
    const counts: Record<string, number> = {};
    for (const r of rects) counts[r.pdfId] = (counts[r.pdfId] ?? 0) + 1;
    return counts;
  };

  const cache           = new TextContentCache();
  const renderer        = new RectRenderer(viewer);
  const contextMenu     = new RectContextMenu();
  const selectionPanel  = new LinkSelectionPanel();
  const overlay         = new RectDrawOverlay(viewer, cache);
  const editOverlay     = new RectEditOverlay(viewer, cache, renderer);
  const tableGridEditor = new TableGridEditor(viewer, cache, renderer);
  const tableCopyModal  = new TableCopyModal();
  const charBboxDebug   = new CharBboxOverlay(viewer, cache);
  const matchRenderer   = new SearchMatchRenderer(viewer);
  const searcher        = new PdfTextSearcher(cache);
  const searchNavigator = createSearchNavigator(
    viewer, selector, matchRenderer, applyFitZoom, onNavigateToPage,
  );

  /** Rectangle the viewer is currently showing; marked as active in the panel. */
  let _focusedRectId: string | null = null;

  const setLinkSelection = (entries: LinkSelectionEntry[]): void => {
    _currentSelection = entries;
    selectionPanel.setEntries(entries);
    selectionPanel.setActiveEntry(_focusedRectId);
    renderer.setSelectedRectangles(entries.map((e) => e.id));
  };

  const clearLinkSelection = (): void => setLinkSelection([]);

  const focusRectangle = (id: string): void => {
    _focusedRectId = id;
    selectionPanel.setActiveEntry(id);
  };

  let lastSearchResults: SearchMatch[] = [];
  let highlightSearchResults: SearchMatch[] = [];
  let focusedMatch: SearchMatch | null = null;
  let searchGeneration = 0;
  let activeSearchSession: ReturnType<PdfTextSearcher["createSession"]> | null = null;
  let searchHasMore = false;
  let searchBatchTimer: ReturnType<typeof setTimeout> | null = null;

  const clearSearchBatchTimer = (): void => {
    if (searchBatchTimer === null) return;
    clearTimeout(searchBatchTimer);
    searchBatchTimer = null;
  };

  const getActivePdfHighlightMatches = (activePdfId: string): SearchMatch[] => {
    const submittedQuery = search.getSubmittedQuery();
    const entry = selector.getEntry(activePdfId);

    const visiblePageIndices = getVisiblePageIndices();

    // Cell-click text is staged rather than submitted. Search only the visible
    // pages so selection changes stay cheap and never populate the cross-document
    // results panel. Scroll and document changes call this again for the new view.
    if (!submittedQuery) {
      const stagedQuery = search.getQuery();
      if (!stagedQuery || !entry) return [];

      return visiblePageIndices.flatMap((pageIndex) =>
        searcher.searchPage(stagedQuery, entry, pageIndex)
      );
    }

    const matches = new Map<string, SearchMatch>();

    for (const match of highlightSearchResults) {
      if (match.pdfId === activePdfId) matches.set(match.id, match);
    }

    if (entry) {
      for (const pageIndex of visiblePageIndices) {
        for (const match of searcher.searchPage(submittedQuery, entry, pageIndex)) {
          matches.set(match.id, match);
        }
      }
    }

    return Array.from(matches.values());
  };

  const applyActivePdfHighlights = (): void => {
    const activePdfId = viewer.getActivePdfId();
    if (!activePdfId) {
      matchRenderer.clearMatches();
      return;
    }

    if (
      focusedMatch
      && focusedMatch.pdfId === activePdfId
      && focusedMatch.pageIndex === currentPage - 1
    ) {
      matchRenderer.setMatches([focusedMatch]);
      matchRenderer.highlightMatch(focusedMatch.id);
      return;
    }

    if (focusedMatch && focusedMatch.pdfId !== activePdfId) {
      focusedMatch = null;
    }

    matchRenderer.setMatches(getActivePdfHighlightMatches(activePdfId));
  };

  onVisiblePageChanged = () => {
    if (!search.getQuery()) return;
    applyActivePdfHighlights();
  };

  const publishSearchResults = (): void => {
    search.setResults(
      lastSearchResults,
      searchHasMore,
      searchHasMore && lastSearchResults.length < SEARCH_RESULTS_PANEL_LIMIT,
    );
    applyActivePdfHighlights();
  };

  const loadSearchBatch = (
    generation: number,
    limit: number,
    scheduleNext: boolean,
  ): void => {
    if (generation !== searchGeneration || !activeSearchSession) return;

    const batch = activeSearchSession.nextBatch(limit, SEARCH_PAGE_BATCH_SIZE);
    if (generation !== searchGeneration) return;

    if (batch.matches.length > 0) {
      highlightSearchResults = highlightSearchResults.concat(batch.matches);

      if (lastSearchResults.length < SEARCH_RESULTS_PANEL_LIMIT) {
        const remainingListSlots = SEARCH_RESULTS_PANEL_LIMIT - lastSearchResults.length;
        lastSearchResults = lastSearchResults.concat(batch.matches.slice(0, remainingListSlots));
      }
    }

    searchHasMore = batch.hasMore || highlightSearchResults.length > lastSearchResults.length;
    publishSearchResults();

    if (!scheduleNext || !batch.hasMore) return;

    searchBatchTimer = setTimeout(() => {
      searchBatchTimer = null;
      loadSearchBatch(generation, SEARCH_RESULT_BATCH_SIZE, true);
    }, SEARCH_BATCH_DELAY_MS);
  };

  const runSearch = (query: string): void => {
    const generation = ++searchGeneration;
    clearSearchBatchTimer();
    focusedMatch = null;
    activeSearchSession = null;
    searchHasMore = false;

    if (!query) {
      search.clearResults();
      matchRenderer.clearMatches();
      lastSearchResults = [];
      highlightSearchResults = [];
      return;
    }

    lastSearchResults = [];
    highlightSearchResults = [];
    activeSearchSession = searcher.createSession(
      query,
      prioritizePdfEntries(selector.getFilteredEntries(), viewer.getActivePdfId()),
    );
    loadSearchBatch(generation, INITIAL_SEARCH_RESULT_LIMIT, true);
  };

  // The filter narrows the document list and cross-document search alike. The
  // open document stays loaded even when it sits outside the selected folder.
  folderFilter.onChange((folderId) => {
    selector.setFolderFilter(folderId);
    const query = search.getSubmittedQuery();
    if (query) runSearch(query);
  });

  search.onQuery(runSearch);
  search.onShowMore(() => {
    clearSearchBatchTimer();
    loadSearchBatch(searchGeneration, SEARCH_RESULT_BATCH_SIZE, false);
  });

  search.onMatchClicked((match) => {
    focusedMatch = match;
    searchNavigator(match, lastSearchResults);
  });

  // Ctrl+F focuses the PDF text search; Ctrl+Shift+F opens the document selector
  // and focuses its filter box. Both are captured because the WebView otherwise
  // hands them to its own find UI.
  document.addEventListener(
    "keydown",
    (e) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || e.key.toLowerCase() !== "f") return;

      e.preventDefault();
      e.stopPropagation();

      if (e.shiftKey) {
        search.hideResults();
        selector.openWithSearchFocus();
        return;
      }

      selector.close();
      search.focus();
    },
    true
  );

  search.disable();

  // Tab/Shift+Tab, Enter/Shift+Enter and Ctrl+Z drive the Excel grid rather than
  // the WebView. Registered after the Ctrl+F handler above so search keeps its key.
  attachExcelKeyBridge();

  contextMenu.attachScrollTarget(viewer.element);

  overlay.onRectCreated((payload) => {
    const linkType = linkTypeSelector.getLinkType();
    const table = linkType === "table"
      ? detectTableGrid(cache.get(payload.pdfId, payload.page), payload.rect)
      : undefined;
    sendLinkRectangleCreated({ ...payload, linkType, ...(table ? { table } : {}) });
    renderer.addRectangle({
      id:    `temp-${Date.now()}`,
      pdfId: payload.pdfId,
      page:  payload.page,
      rect:  payload.rect,
      linkType,
      ...(table ? { table } : {}),
    });
  });

  editOverlay.onRectUpdated((payload) => {
    sendLinkRectangleUpdated(payload);
  });

  tableGridEditor.onTableUpdated((payload) => {
    sendLinkRectangleUpdated(payload);
  });

  tableGridEditor.onCopySelection(async (id) => {
    const entry = renderer.getRectangle(id);
    const totalPages = viewer.getDocument()?.numPages ?? 0;
    if (!entry?.table || totalPages < 1) return;

    const pages = await tableCopyModal.show(entry.page, totalPages);
    if (!pages) return;

    const targets = [];
    for (const page of pages) {
      // Give the viewer a paint opportunity between pages. Table extraction is managed
      // data work, but a large page range should not monopolize the WebView UI thread.
      await new Promise<void>((resolve) => setTimeout(resolve, 0));
      const pageEntries = cache.get(entry.pdfId, page);
      targets.push({
        page,
        table: detectCopiedTable(
          pageEntries,
          entry.rect,
          entry.table.columnBoundaries,
        ),
      });
    }
    sendCopyTableSelection(id, targets);
  });

  renderer.setClickGuard(() => editOverlay.consumeClickSuppression());

  renderer.onRectClicked((id) => {
    // Clicking a rectangle selects its own cell in Excel, ending any multi-cell
    // selection. The host re-publishes that cell's links, so a sum cell backed by
    // several rectangles repopulates the panel straight away.
    focusRectangle(id);
    clearLinkSelection();
    sendLinkRectangleClicked(id);
  });

  renderer.onRectContextMenu((id, x, y) => {
    contextMenu.show(x, y, id);
  });

  contextMenu.onDelete((id) => {
    sendLinkRectangleDeleted(id, true);
  });

  contextMenu.onDeleteKeepData((id) => {
    sendLinkRectangleDeleted(id, false);
  });

  let cacheGeneration = 0;

  viewer.onDocumentChanged(() => {
    const pdfId = viewer.getActivePdfId();
    const doc   = viewer.getDocument();
    if (!pdfId || !doc) return;

    const finish = (): void => {
      charBboxDebug.refresh();
      if (search.getQuery()) {
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

  const navigate = createRectNavigator(
    viewer, selector, renderer, applyFitZoom, onNavigateToPage,
  );

  selectionPanel.onEntryClicked((entry) => {
    _focusedRectId = entry.id;
    navigate(entry.id, entry.pdfId, entry.page);
  });

  connectViewerToHostBridge(
    viewer,
    selector,
    folderFilter,
    cache,
    (indexing) => {
      if (indexing) {
        search.disable();
      } else {
        search.enable();
        const query = search.getSubmittedQuery();
        if (query) {
          runSearch(query);
        } else if (search.getQuery()) {
          applyActivePdfHighlights();
        }
      }
    },
    {
      onLinkedRectangles: (rects) => {
        contextMenu.hide();
        _currentRects = rects;
        renderer.setRectangles(rects);
        selector.updateLinkCounts(computeLinkCounts(rects));
      },
      onLinkedRectangleAdded: (rect) => {
        if (_currentRects.some((current) => current.id === rect.id)) return;
        _currentRects = [..._currentRects, rect];
        renderer.addRectangle(rect);
        selector.updateLinkCounts(computeLinkCounts(_currentRects));
        focusRectangle(rect.id);
        navigate(rect.id, rect.pdfId, rect.page);
      },
      onNavigateToRectangle: (id, pdfId, page) => {
        focusRectangle(id);
        navigate(id, pdfId, page);
      },
      onClearRectangleHighlight: () => { renderer.clearHighlight(); },
      onHighlightRectangle: (id) => { renderer.highlightRectangle(id); },
      onLinkSelectionChanged: setLinkSelection,
      onSetSearchQuery: (query) => {
        // setQuery synchronously cancels any submitted search before the staged
        // visible-page preview is rendered.
        search.setQuery(query);
        applyActivePdfHighlights();
      },
      onLinkRectanglesRemoved: (ids) => {
        contextMenu.hide();
        renderer.removeRectangles(ids);
        const removed = new Set(ids);
        _currentRects = _currentRects.filter((r) => !removed.has(r.id));
        selector.updateLinkCounts(computeLinkCounts(_currentRects));
        setLinkSelection(_currentSelection.filter((e) => !removed.has(e.id)));
      },
      onPageRotationsUpdated: (pdfId, rotations) => {
        selector.updatePdfRotations(pdfId, rotations);
        if (pdfId === viewer.getActivePdfId()) {
          for (const [k, v] of Object.entries(rotations)) {
            viewer.setPageRotation(Number(k), v);
          }
        }
      },
    },
  );

  // ── Floating link-type bar & selection panel ──────────────────────────────

  const linkTypeBar = document.createElement("div");
  linkTypeBar.className = "link-type-bar";

  linkTypeBar.append(linkTypeSelector.element);

  const viewerWrapper = document.createElement("div");
  viewerWrapper.className = "viewer-wrapper";
  viewerWrapper.append(viewer.element, linkTypeBar, selectionPanel.element);

  return { toolbarElement, viewerWrapper };
}
