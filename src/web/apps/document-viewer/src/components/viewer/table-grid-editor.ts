import type { TextContentCache } from "../../services/text-content-cache.js";
import { withExtractedTableCells } from "../../services/table-extractor.js";
import type { LinkRectUpdatedPayload, TableGridData } from "../../types/index.js";
import type { PdfViewer } from "./pdf-viewer.js";
import type { RectRenderer } from "./rect-renderer.js";
import { getLinkResizeCorner } from "./rect-utils.js";

type Axis = "column" | "row";
type TableUpdatedCallback = (payload: LinkRectUpdatedPayload) => void;
type TableActionCallback = (id: string) => void;

interface BoundaryDrag {
  id: string;
  axis: Axis;
  index: number;
  grabOffset: number;
  line: HTMLElement;
  rectElement: HTMLElement;
  original: TableGridData;
}

const TABLE_SELECTOR = ".rect-draw__link--table";
const LINE_SELECTOR = ".table-grid__line";
const PREVIEW_SELECTOR = ".table-grid__preview, .table-grid__add";
const MIN_GAP = 0.01;
const REMOVE_INTENT_ENTER_PX = 12;
const REMOVE_INTENT_EXIT_PX = 18;

function copyTable(table: TableGridData): TableGridData {
  return {
    columnBoundaries: [...table.columnBoundaries],
    rowBoundaries: [...table.rowBoundaries],
    ...(table.cells ? { cells: table.cells.map((row) => [...row]) } : {}),
    ...(table.headerRowCount ? { headerRowCount: table.headerRowCount } : {}),
    ...(table.textLineBoundaries
      ? { textLineBoundaries: table.textLineBoundaries.map((row) => [...row]) }
      : {}),
  };
}

/**
 * Edits the internal grid of persisted Table rectangles. The outer rectangle remains owned
 * by RectEditOverlay; this class owns only internal row/column boundaries and add previews.
 */
export class TableGridEditor {
  private _drag: BoundaryDrag | null = null;
  private _activeRect: HTMLElement | null = null;
  private _activeAxis: Axis = "column";
  private _previewLine: HTMLElement | null = null;
  private _previewAdd: HTMLButtonElement | null = null;
  private _previewRect: HTMLElement | null = null;
  private _previewAxis: Axis | null = null;
  private _removeIntentLine: HTMLElement | null = null;
  private readonly _axisByRectId = new Map<string, Axis>();
  private readonly _callbacks: TableUpdatedCallback[] = [];
  private readonly _copyCallbacks: TableActionCallback[] = [];

  constructor(
    private readonly _viewer: PdfViewer,
    private readonly _cache: TextContentCache,
    private readonly _renderer: RectRenderer,
  ) {
    const element = this._viewer.element;
    element.addEventListener("mousemove", (event: MouseEvent) => this._onMouseMove(event), true);
    element.addEventListener("mousedown", (event: MouseEvent) => this._onMouseDown(event), true);
    element.addEventListener("click", (event: MouseEvent) => this._onClick(event), true);
    element.addEventListener("contextmenu", (event: MouseEvent) => this._onContextMenu(event), true);
    element.addEventListener("mouseleave", () => this._deactivateTable());
  }

  onTableUpdated(callback: TableUpdatedCallback): void {
    this._callbacks.push(callback);
  }

  onCopySelection(callback: TableActionCallback): void {
    this._copyCallbacks.push(callback);
  }

  private _onMouseDown(event: MouseEvent): void {
    if (event.button !== 0 || !(event.target instanceof Element)) return;
    if (event.target.closest(
      ".table-grid__add, .table-grid__remove, .table-grid__axis-toggle",
    )) {
      // Keep the draw and outer-rectangle edit handlers from starting underneath
      // a grid control while its click is still in progress.
      event.stopImmediatePropagation();
      return;
    }
    const line = event.target.closest<HTMLElement>(LINE_SELECTOR);
    if (!line || line.classList.contains("table-grid__preview")) return;

    const rectElement = line.closest<HTMLElement>(TABLE_SELECTOR);
    const id = rectElement?.dataset["rectId"];
    if (!rectElement || !id || id.startsWith("temp-")) return;
    const entry = this._renderer.getRectangle(id);
    if (!entry?.table) return;

    const axis: Axis = line.classList.contains("table-grid__line--column") ? "column" : "row";
    const position = Number(line.dataset["boundaryPosition"]);
    const boundaries = axis === "column"
      ? entry.table.columnBoundaries
      : entry.table.rowBoundaries;
    const index = boundaries.findIndex((value) => Math.abs(value - position) < 0.000001);
    if (index < 0) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    this._clearPreviews();
    line.classList.add("table-grid__line--dragging");
    const grabOffset = position - this._positionForEvent(event, rectElement, axis);
    this._drag = {
      id, axis, index, grabOffset, line, rectElement, original: copyTable(entry.table),
    };
    document.addEventListener("mousemove", this._onDocumentMouseMove);
    document.addEventListener("mouseup", this._onDocumentMouseUp, true);
  }

  private readonly _onDocumentMouseMove = (event: MouseEvent): void => {
    const drag = this._drag;
    if (!drag) return;
    const position = this._dragPositionForEvent(event, drag);
    const boundaries = drag.axis === "column"
      ? drag.original.columnBoundaries
      : drag.original.rowBoundaries;
    const constrained = this._constrain(position, boundaries, drag.index);
    if (drag.axis === "column") drag.line.style.left = `${constrained * 100}%`;
    else drag.line.style.top = `${constrained * 100}%`;
  };

  private readonly _onDocumentMouseUp = (event: MouseEvent): void => {
    document.removeEventListener("mousemove", this._onDocumentMouseMove);
    document.removeEventListener("mouseup", this._onDocumentMouseUp, true);
    const drag = this._drag;
    this._drag = null;
    if (!drag) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    const position = this._dragPositionForEvent(event, drag);
    const table = copyTable(drag.original);
    const boundaries = drag.axis === "column" ? table.columnBoundaries : table.rowBoundaries;
    const nextPosition = this._constrain(position, boundaries, drag.index);
    if (Math.abs(nextPosition - boundaries[drag.index]!) < 0.000001) {
      drag.line.classList.remove("table-grid__line--dragging");
      return;
    }
    boundaries[drag.index] = nextPosition;
    boundaries.sort((a, b) => a - b);
    this._commit(drag.id, table);
  };

  private _onMouseMove(event: MouseEvent): void {
    if (this._drag) return;

    if (!(event.target instanceof Element)) return;
    const rectElement = event.target.closest<HTMLElement>(TABLE_SELECTOR);
    const id = rectElement?.dataset["rectId"];
    if (!rectElement || !id || id.startsWith("temp-")) {
      this._deactivateTable();
      return;
    }

    this._activateTable(rectElement);
    if (rectElement.classList.contains("rect-draw__link--editing")) {
      this._clearPreviews();
      this._clearRemoveIntent();
      return;
    }
    if (event.target.closest(".table-grid__axis-toggle")) {
      this._clearPreviews();
      this._clearRemoveIntent();
      return;
    }

    const position = this._positionForEvent(event, rectElement, this._activeAxis);
    const targetedLine = event.target.closest<HTMLElement>(
      `${LINE_SELECTOR}:not(.table-grid__preview)`,
    );
    const removeIntentLine = targetedLine
      ?? this._findRemoveIntentLine(rectElement, this._activeAxis, position);
    if (removeIntentLine) {
      this._setRemoveIntent(removeIntentLine);
      this._clearPreviews();
      return;
    }

    this._clearRemoveIntent();
    if (getLinkResizeCorner(rectElement, event.clientX, event.clientY)) {
      this._clearPreviews();
      return;
    }
    this._showPreview(rectElement, this._activeAxis, position);
  }

  private _onClick(event: MouseEvent): void {
    if (!(event.target instanceof Element)) return;
    const menuButton = event.target.closest<HTMLElement>(".table-grid__axis-menu-button");
    if (menuButton) {
      event.preventDefault();
      event.stopImmediatePropagation();
      const toggle = menuButton.closest<HTMLElement>(".table-grid__axis-toggle");
      if (toggle) {
        const open = !toggle.classList.contains("table-grid__axis-toggle--open");
        toggle.classList.toggle("table-grid__axis-toggle--open", open);
        menuButton.setAttribute("aria-expanded", String(open));
      }
      return;
    }

    const menuAction = event.target.closest<HTMLElement>(".table-grid__menu-action");
    if (menuAction) {
      event.preventDefault();
      event.stopImmediatePropagation();
      const rectElement = menuAction.closest<HTMLElement>(TABLE_SELECTOR);
      const id = rectElement?.dataset["rectId"];
      if (!id) return;
      this._closeAxisMenu(rectElement);
      if (menuAction.dataset["action"] === "split-detected-rows") {
        this._splitDetectedRows(id);
      } else {
        for (const callback of this._copyCallbacks) callback(id);
      }
      return;
    }

    const axisButton = event.target.closest<HTMLElement>(".table-grid__axis-button");
    if (axisButton) {
      event.preventDefault();
      event.stopImmediatePropagation();
      const rectElement = axisButton.closest<HTMLElement>(TABLE_SELECTOR);
      const axis = axisButton.dataset["axis"] as Axis | undefined;
      if (rectElement && (axis === "column" || axis === "row")) {
        this._activeRect = rectElement;
        this._activeAxis = axis;
        const id = rectElement.dataset["rectId"];
        if (id) this._axisByRectId.set(id, axis);
        this._applyActiveAxis();
        this._clearPreviews();
        this._clearRemoveIntent();
        this._closeAxisMenu(rectElement);
      }
      return;
    }

    this._closeAxisMenu();

    const remove = event.target.closest<HTMLElement>(".table-grid__remove");
    if (remove) {
      event.preventDefault();
      event.stopImmediatePropagation();
      const rectElement = remove.closest<HTMLElement>(TABLE_SELECTOR);
      const id = rectElement?.dataset["rectId"];
      const axis = remove.dataset["axis"] as Axis | undefined;
      const position = Number(remove.dataset["position"]);
      if (id && axis && Number.isFinite(position)) this._removeBoundary(id, axis, position);
      return;
    }

    const add = event.target.closest<HTMLElement>(".table-grid__add");
    if (add) {
      event.preventDefault();
      event.stopImmediatePropagation();
      const rectElement = add.closest<HTMLElement>(TABLE_SELECTOR);
      const id = rectElement?.dataset["rectId"];
      const axis = add.dataset["axis"] as Axis | undefined;
      const position = Number(add.dataset["position"]);
      if (id && axis && Number.isFinite(position)) this._addBoundary(id, axis, position);
      return;
    }

    if (event.target.closest(LINE_SELECTOR)) {
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  }

  private _onContextMenu(event: MouseEvent): void {
    if (!(event.target instanceof Element)) return;
    const line = event.target.closest<HTMLElement>(LINE_SELECTOR);
    if (!line || line.classList.contains("table-grid__preview")) return;
    event.preventDefault();
    event.stopImmediatePropagation();
  }

  private _addBoundary(id: string, axis: Axis, position: number): void {
    const entry = this._renderer.getRectangle(id);
    if (!entry?.table) return;
    const table = copyTable(entry.table);
    const boundaries = axis === "column" ? table.columnBoundaries : table.rowBoundaries;
    if (boundaries.some((value) => Math.abs(value - position) < MIN_GAP)) return;
    boundaries.push(Math.min(1 - MIN_GAP, Math.max(MIN_GAP, position)));
    boundaries.sort((a, b) => a - b);
    this._commit(id, table);
  }

  private _removeBoundary(id: string, axis: Axis, position: number): void {
    const entry = this._renderer.getRectangle(id);
    if (!entry?.table) return;
    const table = copyTable(entry.table);
    const boundaries = axis === "column" ? table.columnBoundaries : table.rowBoundaries;
    const index = boundaries.findIndex((value) => Math.abs(value - position) < 0.000001);
    if (index < 0) return;
    boundaries.splice(index, 1);
    this._commit(id, table);
  }

  private _splitDetectedRows(id: string): void {
    const entry = this._renderer.getRectangle(id);
    if (!entry?.table?.textLineBoundaries) return;
    const detectedBoundaries = entry.table.textLineBoundaries.flat();
    const table = copyTable(entry.table);
    for (const boundary of detectedBoundaries) {
      if (typeof boundary !== "number") continue;
      if (boundary <= MIN_GAP || boundary >= 1 - MIN_GAP) continue;
      if (!table.rowBoundaries.some((value) => Math.abs(value - boundary) < MIN_GAP)) {
        table.rowBoundaries.push(boundary);
      }
    }
    table.rowBoundaries.sort((a, b) => a - b);
    delete table.headerRowCount;
    delete table.textLineBoundaries;
    this._commit(id, table);
  }

  private _commit(id: string, table: TableGridData): void {
    const entry = this._renderer.getRectangle(id);
    if (!entry) return;
    this._clearRemoveIntent();
    const extracted = withExtractedTableCells(
      this._cache.get(entry.pdfId, entry.page), entry.rect, table,
    );
    this._renderer.updateTable(id, extracted);
    const payload: LinkRectUpdatedPayload = {
      id,
      pdfId: entry.pdfId,
      page: entry.page,
      rect: entry.rect,
      text: "",
      table: extracted,
    };
    for (const callback of this._callbacks) callback(payload);
  }

  private _showPreview(rectElement: HTMLElement, axis: Axis, position: number): void {
    if (
      !this._previewLine?.isConnected
      || !this._previewAdd?.isConnected
      || this._previewRect !== rectElement
      || this._previewAxis !== axis
    ) {
      this._clearPreviews();

      const line = document.createElement("div");
      line.className = `table-grid__line table-grid__line--${axis} table-grid__preview`;
      rectElement.appendChild(line);

      const add = document.createElement("button");
      add.type = "button";
      add.className = `table-grid__add table-grid__add--${axis}`;
      add.textContent = "+";
      add.title = `Add ${axis}`;
      add.setAttribute("aria-label", `Add ${axis}`);
      add.dataset["axis"] = axis;
      rectElement.appendChild(add);

      this._previewLine = line;
      this._previewAdd = add;
      this._previewRect = rectElement;
      this._previewAxis = axis;
    }

    const percent = `${position * 100}%`;
    if (axis === "column") {
      this._previewLine.style.left = percent;
      this._previewAdd.style.left = percent;
    } else {
      this._previewLine.style.top = percent;
      this._previewAdd.style.top = percent;
    }
    this._previewAdd.dataset["position"] = String(position);
  }

  private _clearPreviews(): void {
    for (const element of Array.from(
      this._viewer.element.querySelectorAll<HTMLElement>(PREVIEW_SELECTOR),
    )) element.remove();
    this._previewLine = null;
    this._previewAdd = null;
    this._previewRect = null;
    this._previewAxis = null;
  }

  private _findRemoveIntentLine(
    rectElement: HTMLElement,
    axis: Axis,
    position: number,
  ): HTMLElement | null {
    const axisClass = `.table-grid__line--${axis}:not(.table-grid__preview)`;
    const lines = Array.from(rectElement.querySelectorAll<HTMLElement>(axisClass));
    const axisSize = axis === "column"
      ? rectElement.getBoundingClientRect().width
      : rectElement.getBoundingClientRect().height;

    const current = this._removeIntentLine;
    if (current?.isConnected && current.closest(TABLE_SELECTOR) === rectElement) {
      const currentPosition = Number(current.dataset["boundaryPosition"]);
      if (
        Number.isFinite(currentPosition)
        && Math.abs(currentPosition - position) * axisSize <= REMOVE_INTENT_EXIT_PX
      ) return current;
    }

    let nearest: HTMLElement | null = null;
    let nearestDistance = Number.POSITIVE_INFINITY;
    for (const line of lines) {
      const boundaryPosition = Number(line.dataset["boundaryPosition"]);
      if (!Number.isFinite(boundaryPosition)) continue;
      const distance = Math.abs(boundaryPosition - position) * axisSize;
      if (distance < nearestDistance) {
        nearest = line;
        nearestDistance = distance;
      }
    }
    return nearestDistance <= REMOVE_INTENT_ENTER_PX ? nearest : null;
  }

  private _setRemoveIntent(line: HTMLElement): void {
    if (this._removeIntentLine === line) return;
    this._clearRemoveIntent();
    this._removeIntentLine = line;
    line.classList.add("table-grid__line--remove-intent");
  }

  private _clearRemoveIntent(): void {
    this._removeIntentLine?.classList.remove("table-grid__line--remove-intent");
    this._removeIntentLine = null;
  }

  private _activateTable(rectElement: HTMLElement): void {
    if (this._activeRect === rectElement) {
      this._ensureAxisToggle(rectElement);
      return;
    }
    this._deactivateTable();
    this._activeRect = rectElement;
    const id = rectElement.dataset["rectId"];
    this._activeAxis = id ? this._axisByRectId.get(id) ?? "column" : "column";
    this._ensureAxisToggle(rectElement);
    this._applyActiveAxis();
  }

  private _deactivateTable(): void {
    if (this._drag) return;
    this._clearPreviews();
    this._clearRemoveIntent();
    this._activeRect?.classList.remove("table-grid--editing-rows");
    this._activeRect?.querySelector(".table-grid__axis-toggle")?.remove();
    this._activeRect = null;
    this._activeAxis = "column";
  }

  private _ensureAxisToggle(rectElement: HTMLElement): void {
    if (rectElement.querySelector(".table-grid__axis-toggle")) return;
    const toggle = document.createElement("div");
    toggle.className = "table-grid__axis-toggle";
    const menuButton = document.createElement("button");
    menuButton.type = "button";
    menuButton.className = "table-grid__axis-menu-button";
    menuButton.textContent = "•••";
    menuButton.title = "Table grid options";
    menuButton.setAttribute("aria-label", "Table grid options");
    menuButton.setAttribute("aria-expanded", "false");
    toggle.appendChild(menuButton);

    const options = document.createElement("div");
    options.className = "table-grid__axis-options";
    options.setAttribute("role", "menu");
    options.setAttribute("aria-label", "Table options");
    const axisGroup = document.createElement("div");
    axisGroup.className = "table-grid__axis-group";
    axisGroup.setAttribute("role", "group");
    axisGroup.setAttribute("aria-label", "Edit table boundaries");
    for (const axis of ["column", "row"] as const) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "table-grid__axis-button";
      button.textContent = axis === "column" ? "Cols" : "Rows";
      button.title = axis === "column" ? "Edit columns" : "Edit rows";
      button.dataset["axis"] = axis;
      axisGroup.appendChild(button);
    }
    options.appendChild(axisGroup);

    const divider = document.createElement("div");
    divider.className = "table-grid__menu-divider";
    options.appendChild(divider);
    const id = rectElement.dataset["rectId"];
    const table = id ? this._renderer.getRectangle(id)?.table : undefined;
    if (table?.textLineBoundaries?.some((boundaries) => boundaries.length > 0)) {
      options.append(this._menuAction("split-detected-rows", "Split detected rows"));
    }
    const copyAction = this._menuAction("copy", "Copy table selection…");
    if ((this._viewer.getDocument()?.numPages ?? 0) < 2) {
      copyAction.disabled = true;
      copyAction.title = "This document has no other pages";
    }
    options.append(copyAction);
    toggle.appendChild(options);
    rectElement.appendChild(toggle);
    this._applyActiveAxis();
  }

  private _closeAxisMenu(rectElement = this._activeRect): void {
    const toggle = rectElement?.querySelector<HTMLElement>(".table-grid__axis-toggle");
    if (!toggle) return;
    toggle.classList.remove("table-grid__axis-toggle--open");
    toggle.querySelector(".table-grid__axis-menu-button")?.setAttribute("aria-expanded", "false");
  }

  private _applyActiveAxis(): void {
    const rectElement = this._activeRect;
    if (!rectElement) return;
    rectElement.classList.toggle("table-grid--editing-rows", this._activeAxis === "row");
    for (const button of Array.from(
      rectElement.querySelectorAll<HTMLElement>(".table-grid__axis-button"),
    )) {
      const active = button.dataset["axis"] === this._activeAxis;
      button.classList.toggle("table-grid__axis-button--active", active);
      button.setAttribute("aria-pressed", String(active));
    }
  }

  private _menuAction(action: string, label: string): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "table-grid__menu-action";
    button.textContent = label;
    button.dataset["action"] = action;
    button.setAttribute("role", "menuitem");
    return button;
  }

  private _positionForEvent(event: MouseEvent, rectElement: HTMLElement, axis: Axis): number {
    const bounds = rectElement.getBoundingClientRect();
    const raw = axis === "column"
      ? (event.clientX - bounds.left) / bounds.width
      : (event.clientY - bounds.top) / bounds.height;
    return Math.min(1 - MIN_GAP, Math.max(MIN_GAP, raw));
  }

  private _dragPositionForEvent(event: MouseEvent, drag: BoundaryDrag): number {
    const position = this._positionForEvent(event, drag.rectElement, drag.axis) + drag.grabOffset;
    return Math.min(1 - MIN_GAP, Math.max(MIN_GAP, position));
  }

  private _constrain(position: number, boundaries: number[], index: number): number {
    const before = index > 0 ? boundaries[index - 1]! + MIN_GAP : MIN_GAP;
    const after = index < boundaries.length - 1 ? boundaries[index + 1]! - MIN_GAP : 1 - MIN_GAP;
    return Math.min(after, Math.max(before, position));
  }
}
