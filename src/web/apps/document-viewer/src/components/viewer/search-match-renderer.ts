import type { PdfViewer } from "./pdf-viewer.js";
import type { SearchMatch } from "../../types/index.js";
import { ensureOverlayLayer } from "./page-renderer.js";
import { applyNormalizedRectToElement } from "./rect-utils.js";

const MATCH_CLASS        = "search-match";
const MATCH_CANVAS_CLASS = "search-match-canvas";
const ACTIVE_MATCH_CLASS = "search-match--active";

/**
 * Renders search-hit rectangles as absolutely-positioned overlay divs on the
 * active PDF's page wrappers. Only matches for the currently loaded PDF are
 * shown; all hits share one style until a specific match is activated.
 */
export class SearchMatchRenderer {
  private _matchesByPdf = new Map<string, Map<number, SearchMatch[]>>();
  private _activeMatchId: string | null = null;

  constructor(private readonly _viewer: PdfViewer) {
    this._viewer.onDocumentChanged(() => this._renderAll());
  }

  setMatches(matches: SearchMatch[]): void {
    this._matchesByPdf = groupMatchesByPdfAndPage(matches);
    this._activeMatchId = null;
    this._renderAll();
  }

  highlightMatch(matchId: string): void {
    this._activeMatchId = matchId;
    this._applyActiveHighlight();
  }

  clearActiveHighlight(): void {
    this._activeMatchId = null;
    this._applyActiveHighlight();
  }

  clearMatches(): void {
    this._matchesByPdf.clear();
    this._activeMatchId = null;
    this._removeAllOverlays();
  }

  private _renderAll(): void {
    this._removeAllOverlays();

    const pdfId = this._viewer.getActivePdfId();
    if (!pdfId) return;

    const pageMatches = this._matchesByPdf.get(pdfId);
    if (!pageMatches) return;

    for (const { pageNumber, wrapper } of this._viewer.getPageLayout()) {
      this._renderPage(wrapper, pageNumber - 1, pageMatches);
    }

    this._applyActiveHighlight();
  }

  private _renderPage(
    wrapper: HTMLDivElement,
    pageIndex: number,
    pageMatches: Map<number, SearchMatch[]>,
  ): void {
    const matchesOnPage = pageMatches.get(pageIndex) ?? [];
    if (matchesOnPage.length === 0) return;

    const overlayLayer = ensureOverlayLayer(wrapper);
    const canvas = document.createElement("canvas");
    canvas.className = MATCH_CANVAS_CLASS;
    canvas.width = Math.max(1, Math.round(wrapper.clientWidth));
    canvas.height = Math.max(1, Math.round(wrapper.clientHeight));
    overlayLayer.appendChild(canvas);

    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    ctx.fillStyle = "rgba(250, 204, 21, 0.35)";

    for (const match of matchesOnPage) {
      const { x, y, width, height } = match.highlightRect;
      ctx.fillRect(
        x * canvas.width,
        y * canvas.height,
        width * canvas.width,
        height * canvas.height,
      );
    }
  }

  private _applyActiveHighlight(): void {
    for (const el of Array.from(
      this._viewer.element.querySelectorAll<HTMLElement>(`.${ACTIVE_MATCH_CLASS}`),
    )) {
      el.classList.remove(ACTIVE_MATCH_CLASS);
    }

    if (this._activeMatchId === null) return;

    for (const pageMatches of this._matchesByPdf.values()) {
      for (const matches of pageMatches.values()) {
        const match = matches.find((candidate) => candidate.id === this._activeMatchId);
        if (!match) continue;

        const wrapper = this._viewer.element.querySelector<HTMLDivElement>(
          `[data-page="${match.pageIndex + 1}"]`,
        );
        if (!wrapper) return;

        const div = document.createElement("div");
        div.className = `${MATCH_CLASS} ${ACTIVE_MATCH_CLASS}`;
        div.dataset["matchId"] = match.id;
        applyNormalizedRectToElement(div, match.highlightRect);
        ensureOverlayLayer(wrapper).appendChild(div);
        return;
      }
    }
  }

  private _removeAllOverlays(): void {
    for (const el of Array.from(
      this._viewer.element.querySelectorAll(`.${MATCH_CLASS}, .${MATCH_CANVAS_CLASS}`),
    )) {
      el.remove();
    }
  }
}

function groupMatchesByPdfAndPage(matches: SearchMatch[]): Map<string, Map<number, SearchMatch[]>> {
  const byPdf = new Map<string, Map<number, SearchMatch[]>>();

  for (const match of matches) {
    let byPage = byPdf.get(match.pdfId);
    if (!byPage) {
      byPage = new Map<number, SearchMatch[]>();
      byPdf.set(match.pdfId, byPage);
    }

    const pageMatches = byPage.get(match.pageIndex) ?? [];
    pageMatches.push(match);
    byPage.set(match.pageIndex, pageMatches);
  }

  return byPdf;
}
