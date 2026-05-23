import type { PdfViewer } from "./pdf-viewer.js";
import type { SearchMatch } from "../../types/index.js";

const MATCH_CLASS       = "search-match";
const ACTIVE_MATCH_CLASS = "search-match--active";

/**
 * Renders search-hit rectangles as absolutely-positioned overlay divs on the
 * active PDF's page wrappers. Only matches for the currently loaded PDF are
 * shown; all hits share one style until a specific match is activated.
 */
export class SearchMatchRenderer {
  private _matches: SearchMatch[] = [];
  private _activeMatchId: string | null = null;

  constructor(private readonly _viewer: PdfViewer) {
    this._viewer.onDocumentChanged(() => this._renderAll());
  }

  setMatches(matches: SearchMatch[]): void {
    this._matches = matches;
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
    this._matches = [];
    this._activeMatchId = null;
    this._removeAllOverlays();
  }

  private _renderAll(): void {
    this._removeAllOverlays();

    const pdfId = this._viewer.getActivePdfId();
    if (!pdfId) return;

    const pageMatches = this._matches.filter((m) => m.pdfId === pdfId);
    for (const { pageNumber, wrapper } of this._viewer.getPageLayout()) {
      this._renderPage(wrapper, pageNumber - 1, pageMatches);
    }

    this._applyActiveHighlight();
  }

  private _renderPage(
    wrapper: HTMLDivElement,
    pageIndex: number,
    pageMatches: SearchMatch[],
  ): void {
    const matchesOnPage = pageMatches.filter((m) => m.pageIndex === pageIndex);

    for (const match of matchesOnPage) {
      const div = document.createElement("div");
      div.className = MATCH_CLASS;
      div.dataset["matchId"] = match.id;
      div.style.left   = `${match.highlightRect.x      * 100}%`;
      div.style.top    = `${match.highlightRect.y      * 100}%`;
      div.style.width  = `${match.highlightRect.width  * 100}%`;
      div.style.height = `${match.highlightRect.height * 100}%`;
      wrapper.appendChild(div);
    }
  }

  private _applyActiveHighlight(): void {
    for (const el of Array.from(
      this._viewer.element.querySelectorAll<HTMLElement>(`.${ACTIVE_MATCH_CLASS}`),
    )) {
      el.classList.remove(ACTIVE_MATCH_CLASS);
    }

    if (this._activeMatchId === null) return;

    const el = this._viewer.element.querySelector<HTMLElement>(
      `[data-match-id="${CSS.escape(this._activeMatchId)}"]`,
    );
    el?.classList.add(ACTIVE_MATCH_CLASS);
  }

  private _removeAllOverlays(): void {
    for (const el of Array.from(
      this._viewer.element.querySelectorAll(`.${MATCH_CLASS}`),
    )) {
      el.remove();
    }
  }
}
