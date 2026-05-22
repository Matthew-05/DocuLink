import type { PdfViewer } from "./pdf-viewer.js";
import type { LinkedRectEntry } from "../../types/index.js";

const LINK_CLASS      = "rect-draw__link";
const HIGHLIGHT_CLASS = "rect-draw__link--highlighted";

/**
 * Renders persisted link rectangles as absolutely-positioned overlay divs
 * inside each `.viewer__page` wrapper.
 *
 * Positioning uses CSS percentages so overlays scale automatically when the
 * viewer is zoomed (the wrapper resizes; the canvas inside is replaced, but
 * the wrapper div and its children persist).
 */
export class RectRenderer {
  private _rects: LinkedRectEntry[] = [];
  private readonly _onRectClickedCallbacks: Array<(id: string) => void> = [];
  private readonly _onRectContextMenuCallbacks: Array<(id: string, x: number, y: number) => void> = [];
  private _highlightedId: string | null = null;

  constructor(private readonly _viewer: PdfViewer) {
    this._viewer.onDocumentChanged(() => this._renderAll());
  }

  /** Registers a callback invoked when the user clicks a link rectangle overlay. */
  onRectClicked(cb: (id: string) => void): void {
    this._onRectClickedCallbacks.push(cb);
  }

  /** Registers a callback invoked when the user right-clicks a link rectangle overlay. */
  onRectContextMenu(cb: (id: string, x: number, y: number) => void): void {
    this._onRectContextMenuCallbacks.push(cb);
  }

  /**
   * Highlights the rectangle with the given id. Any previously highlighted
   * rectangle is cleared immediately so only one is ever highlighted at a time.
   * The highlight persists until the next call to highlightRectangle or until
   * the renderer re-renders (e.g. document or PDF change).
   */
  highlightRectangle(id: string): void {
    this._highlightedId = id;
    this._applyHighlight();
  }

  clearHighlight(): void {
    this._highlightedId = null;
    this._applyHighlight();
  }

  /** Returns whether the given rectangle id is in the renderer's stored set. */
  hasRectangle(id: string): boolean {
    return this._rects.some((r) => r.id === id);
  }

  private _applyHighlight(): void {
    for (const el of Array.from(
      this._viewer.element.querySelectorAll<HTMLElement>(`.${HIGHLIGHT_CLASS}`),
    )) {
      el.classList.remove(HIGHLIGHT_CLASS);
    }

    if (this._highlightedId === null) return;

    const el = this._viewer.element.querySelector<HTMLElement>(
      `[data-rect-id="${CSS.escape(this._highlightedId)}"]`,
    );
    el?.classList.add(HIGHLIGHT_CLASS);
  }

  /** Replaces all stored rectangles and re-renders. */
  setRectangles(rects: LinkedRectEntry[]): void {
    this._rects = rects;
    if (this._highlightedId !== null && !rects.some((r) => r.id === this._highlightedId)) {
      this._highlightedId = null;
    }
    this._renderAll();
  }

  /**
   * Appends a single rectangle and re-renders.
   * Used for optimistic rendering immediately after a user draws a rect,
   * before the host confirms persistence.
   */
  addRectangle(rect: LinkedRectEntry): void {
    this._rects = [...this._rects, rect];
    this._renderAll();
  }

  /**
   * Removes one or more rectangles by id. Updates internal state and removes
   * matching overlay divs from the DOM when present (active PDF only).
   */
  removeRectangles(ids: string[]): void {
    if (ids.length === 0) return;

    const idSet = new Set(ids);
    this._rects = this._rects.filter((r) => !idSet.has(r.id));

    for (const id of ids) {
      const el = this._viewer.element.querySelector<HTMLElement>(
        `[data-rect-id="${CSS.escape(id)}"]`,
      );
      el?.remove();
    }

    if (this._highlightedId !== null && idSet.has(this._highlightedId)) {
      this._highlightedId = null;
    }
  }

  private _renderAll(): void {
    const pdfId = this._viewer.getActivePdfId();
    if (!pdfId) return;

    for (const { pageNumber, wrapper } of this._viewer.getPageLayout()) {
      this._renderPage(wrapper, pageNumber - 1, pdfId);
    }

    this._applyHighlight();
  }

  private _renderPage(
    wrapper: HTMLDivElement,
    pageIndex: number,
    pdfId: string,
  ): void {
    // Remove stale overlays before re-adding.
    for (const el of Array.from(wrapper.querySelectorAll(`.${LINK_CLASS}`))) {
      el.remove();
    }

    const pageRects = this._rects.filter(
      (r) => r.pdfId === pdfId && r.page === pageIndex,
    );

    for (const entry of pageRects) {
      const div = document.createElement("div");
      div.className = LINK_CLASS;
      div.dataset["rectId"] = entry.id;
      div.style.left   = `${entry.rect.x      * 100}%`;
      div.style.top    = `${entry.rect.y      * 100}%`;
      div.style.width  = `${entry.rect.width  * 100}%`;
      div.style.height = `${entry.rect.height * 100}%`;
      div.addEventListener("click", (e) => {
        e.stopPropagation();
        this.highlightRectangle(entry.id);
        for (const cb of this._onRectClickedCallbacks) cb(entry.id);
      });
      div.addEventListener("contextmenu", (e) => {
        e.preventDefault();
        e.stopPropagation();
        for (const cb of this._onRectContextMenuCallbacks) {
          cb(entry.id, e.clientX, e.clientY);
        }
      });
      wrapper.appendChild(div);
    }
  }
}
