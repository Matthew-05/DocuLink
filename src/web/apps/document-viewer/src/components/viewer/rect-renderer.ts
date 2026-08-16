import type { PdfViewer } from "./pdf-viewer.js";
import type { LinkedRectEntry, NormalizedRect } from "../../types/index.js";
import { ensureOverlayLayer } from "./page-renderer.js";
import { applyNormalizedRectToElement } from "./rect-utils.js";

const LINK_CLASS      = "rect-draw__link";
const HIGHLIGHT_CLASS = "rect-draw__link--highlighted";
const SELECTED_CLASS  = "rect-draw__link--selected";
const LINK_TYPE_CLASSES = {
  auto: "rect-draw__link--auto",
  raw:  "rect-draw__link--raw",
  sum:  "rect-draw__link--sum",
  table: "rect-draw__link--table",
} as const;

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
  /** The single focused rectangle (last navigated to or clicked). */
  private _highlightedId: string | null = null;
  /** Every rectangle in the current multi-cell Excel selection. */
  private _selectedIds: string[] = [];
  /** When set, the next click callback checks this before firing navigation. */
  private _clickGuard: (() => boolean) | null = null;

  constructor(private readonly _viewer: PdfViewer) {
    this._viewer.onDocumentChanged(() => this._renderAll());
  }

  /** Registers a guard invoked before click navigation; return true to suppress. */
  setClickGuard(guard: (() => boolean) | null): void {
    this._clickGuard = guard;
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
   * Focuses the rectangle with the given id. Any previously focused rectangle is
   * cleared immediately so only one is ever focused at a time. The focus persists
   * until the next call to highlightRectangle or until the renderer re-renders
   * (e.g. document or PDF change). Independent of the selection set, so navigating
   * within a multi-cell selection does not clear the other selected rectangles.
   */
  highlightRectangle(id: string): void {
    this._highlightedId = id;
    this._applyHighlight();
  }

  /**
   * Marks every rectangle in the given set as selected — used when the user
   * selects several linked cells in Excel at once. Pass an empty array to clear.
   */
  setSelectedRectangles(ids: string[]): void {
    this._selectedIds = [...ids];
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

  getRectangle(id: string): LinkedRectEntry | undefined {
    return this._rects.find((r) => r.id === id);
  }

  /** Patches a single rectangle's geometry in state and the DOM. */
  updateRectangle(id: string, rect: NormalizedRect): void {
    const index = this._rects.findIndex((r) => r.id === id);
    if (index < 0) return;

    this._rects[index] = { ...this._rects[index]!, rect: { ...rect } };

    const el = this._viewer.element.querySelector<HTMLElement>(
      `[data-rect-id="${CSS.escape(id)}"]`,
    );
    if (el) applyNormalizedRectToElement(el, rect);
  }

  /** Replaces a table grid in state and re-renders its rectangle. */
  updateTable(id: string, table: NonNullable<LinkedRectEntry["table"]>): void {
    const entry = this._rects.find((rect) => rect.id === id);
    if (!entry) return;
    entry.table = table;
    const element = this._findElement(id);
    if (!element) return;
    for (const child of Array.from(
      element.querySelectorAll<HTMLElement>(".table-grid__line, .table-grid__add"),
    )) child.remove();
    this._appendTableGrid(element, entry);
  }

  private _applyHighlight(): void {
    for (const el of Array.from(
      this._viewer.element.querySelectorAll<HTMLElement>(
        `.${HIGHLIGHT_CLASS}, .${SELECTED_CLASS}`,
      ),
    )) {
      el.classList.remove(HIGHLIGHT_CLASS, SELECTED_CLASS);
    }

    for (const id of this._selectedIds) {
      this._findElement(id)?.classList.add(SELECTED_CLASS);
    }

    if (this._highlightedId === null) return;
    this._findElement(this._highlightedId)?.classList.add(HIGHLIGHT_CLASS);
  }

  private _findElement(id: string): HTMLElement | null {
    return this._viewer.element.querySelector<HTMLElement>(
      `[data-rect-id="${CSS.escape(id)}"]`,
    );
  }

  /** Replaces all stored rectangles and re-renders. */
  setRectangles(rects: LinkedRectEntry[]): void {
    this._rects = rects;
    const live = new Set(rects.map((r) => r.id));
    if (this._highlightedId !== null && !live.has(this._highlightedId)) {
      this._highlightedId = null;
    }
    this._selectedIds = this._selectedIds.filter((id) => live.has(id));
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
    this._selectedIds = this._selectedIds.filter((id) => !idSet.has(id));
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

    const overlayLayer = ensureOverlayLayer(wrapper);

    for (const entry of pageRects) {
      const div = document.createElement("div");
      div.className = LINK_CLASS;
      div.classList.add(LINK_TYPE_CLASSES[entry.linkType ?? "auto"]);
      div.dataset["rectId"] = entry.id;
      applyNormalizedRectToElement(div, entry.rect);
      this._appendTableGrid(div, entry);
      div.addEventListener("click", (e) => {
        e.stopPropagation();
        if (this._clickGuard?.()) return;
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
      overlayLayer.appendChild(div);
    }
  }

  private _appendTableGrid(div: HTMLElement, entry: LinkedRectEntry): void {
    if (entry.linkType !== "table" || !entry.table) return;
    for (const position of entry.table.columnBoundaries) {
      const line = document.createElement("div");
      line.className = "table-grid__line table-grid__line--column";
      line.dataset["boundaryPosition"] = String(position);
      line.style.left = `${position * 100}%`;
      this._appendRemoveControl(line, "column", position);
      div.appendChild(line);
    }
    for (const position of entry.table.rowBoundaries) {
      const line = document.createElement("div");
      line.className = "table-grid__line table-grid__line--row";
      line.dataset["boundaryPosition"] = String(position);
      line.style.top = `${position * 100}%`;
      this._appendRemoveControl(line, "row", position);
      div.appendChild(line);
    }
  }

  private _appendRemoveControl(
    line: HTMLElement,
    axis: "column" | "row",
    position: number,
  ): void {
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = `table-grid__remove table-grid__remove--${axis}`;
    remove.textContent = "×";
    remove.title = `Remove ${axis}`;
    remove.setAttribute("aria-label", `Remove ${axis}`);
    remove.dataset["axis"] = axis;
    remove.dataset["position"] = String(position);
    line.appendChild(remove);
  }

}
