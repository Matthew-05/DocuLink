import type { TextContentCache } from "../../services/text-content-cache.js";
import { withExtractedTableCells } from "../../services/table-extractor.js";
import type { LinkRectUpdatedPayload, LinkedRectEntry, TableGridData } from "../../types/index.js";
import type { PdfViewer } from "./pdf-viewer.js";
import type { RectRenderer } from "./rect-renderer.js";

type Axis = "column" | "row";
type TableUpdatedCallback = (payload: LinkRectUpdatedPayload) => void;

interface BoundaryDrag {
  id: string;
  axis: Axis;
  index: number;
  line: HTMLElement;
  rectElement: HTMLElement;
  original: TableGridData;
}

const TABLE_SELECTOR = ".rect-draw__link--table";
const LINE_SELECTOR = ".table-grid__line";
const MIN_GAP = 0.01;

function copyTable(table: TableGridData): TableGridData {
  return {
    columnBoundaries: [...table.columnBoundaries],
    rowBoundaries: [...table.rowBoundaries],
    ...(table.cells ? { cells: table.cells.map((row) => [...row]) } : {}),
  };
}

/**
 * Edits the internal grid of persisted Table rectangles. The outer rectangle remains owned
 * by RectEditOverlay; this class owns only internal row/column boundaries and add previews.
 */
export class TableGridEditor {
  private _drag: BoundaryDrag | null = null;
  private readonly _callbacks: TableUpdatedCallback[] = [];

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
    element.addEventListener("mouseleave", () => this._clearPreviews());
  }

  onTableUpdated(callback: TableUpdatedCallback): void {
    this._callbacks.push(callback);
  }

  private _onMouseDown(event: MouseEvent): void {
    if (event.button !== 0 || !(event.target instanceof Element)) return;
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
    this._drag = { id, axis, index, line, rectElement, original: copyTable(entry.table) };
    document.addEventListener("mouseup", this._onDocumentMouseUp, true);
  }

  private readonly _onDocumentMouseUp = (event: MouseEvent): void => {
    document.removeEventListener("mouseup", this._onDocumentMouseUp, true);
    const drag = this._drag;
    this._drag = null;
    if (!drag) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    const position = this._positionForEvent(event, drag.rectElement, drag.axis);
    const table = copyTable(drag.original);
    const boundaries = drag.axis === "column" ? table.columnBoundaries : table.rowBoundaries;
    boundaries[drag.index] = this._constrain(position, boundaries, drag.index);
    boundaries.sort((a, b) => a - b);
    this._commit(drag.id, table);
  };

  private _onMouseMove(event: MouseEvent): void {
    if (this._drag) {
      const position = this._positionForEvent(event, this._drag.rectElement, this._drag.axis);
      const boundaries = this._drag.axis === "column"
        ? this._drag.original.columnBoundaries
        : this._drag.original.rowBoundaries;
      const constrained = this._constrain(position, boundaries, this._drag.index);
      if (this._drag.axis === "column") this._drag.line.style.left = `${constrained * 100}%`;
      else this._drag.line.style.top = `${constrained * 100}%`;
      return;
    }

    if (!(event.target instanceof Element)) return;
    const rectElement = event.target.closest<HTMLElement>(TABLE_SELECTOR);
    const id = rectElement?.dataset["rectId"];
    if (!rectElement || !id || id.startsWith("temp-")) {
      this._clearPreviews();
      return;
    }

    const x = this._positionForEvent(event, rectElement, "column");
    const y = this._positionForEvent(event, rectElement, "row");
    this._showPreviews(rectElement, x, y);
  }

  private _onClick(event: MouseEvent): void {
    if (!(event.target instanceof Element)) return;
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
    const rectElement = line.closest<HTMLElement>(TABLE_SELECTOR);
    const id = rectElement?.dataset["rectId"];
    if (!id) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    const axis: Axis = line.classList.contains("table-grid__line--column") ? "column" : "row";
    const position = Number(line.dataset["boundaryPosition"]);
    const entry = this._renderer.getRectangle(id);
    if (!entry?.table) return;
    const table = copyTable(entry.table);
    const boundaries = axis === "column" ? table.columnBoundaries : table.rowBoundaries;
    const index = boundaries.findIndex((value) => Math.abs(value - position) < 0.000001);
    if (index < 0) return;
    boundaries.splice(index, 1);
    this._commit(id, table);
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

  private _commit(id: string, table: TableGridData): void {
    const entry = this._renderer.getRectangle(id);
    if (!entry) return;
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

  private _showPreviews(rectElement: HTMLElement, x: number, y: number): void {
    this._clearPreviews(rectElement);
    this._appendPreview(rectElement, "column", x);
    this._appendPreview(rectElement, "row", y);
  }

  private _appendPreview(rectElement: HTMLElement, axis: Axis, position: number): void {
    const line = document.createElement("div");
    line.className = `table-grid__line table-grid__line--${axis} table-grid__preview`;
    if (axis === "column") line.style.left = `${position * 100}%`;
    else line.style.top = `${position * 100}%`;
    rectElement.appendChild(line);

    const add = document.createElement("button");
    add.type = "button";
    add.className = `table-grid__add table-grid__add--${axis}`;
    add.textContent = "+";
    add.title = `Add ${axis}`;
    add.dataset["axis"] = axis;
    add.dataset["position"] = String(position);
    if (axis === "column") add.style.left = `${position * 100}%`;
    else add.style.top = `${position * 100}%`;
    rectElement.appendChild(add);
  }

  private _clearPreviews(except?: HTMLElement): void {
    for (const element of Array.from(
      this._viewer.element.querySelectorAll<HTMLElement>(".table-grid__preview, .table-grid__add"),
    )) {
      if (except && element.parentElement === except) continue;
      element.remove();
    }
    if (except) {
      for (const element of Array.from(
        except.querySelectorAll<HTMLElement>(".table-grid__preview, .table-grid__add"),
      )) element.remove();
    }
  }

  private _positionForEvent(event: MouseEvent, rectElement: HTMLElement, axis: Axis): number {
    const bounds = rectElement.getBoundingClientRect();
    const raw = axis === "column"
      ? (event.clientX - bounds.left) / bounds.width
      : (event.clientY - bounds.top) / bounds.height;
    return Math.min(1 - MIN_GAP, Math.max(MIN_GAP, raw));
  }

  private _constrain(position: number, boundaries: number[], index: number): number {
    const before = index > 0 ? boundaries[index - 1]! + MIN_GAP : MIN_GAP;
    const after = index < boundaries.length - 1 ? boundaries[index + 1]! - MIN_GAP : 1 - MIN_GAP;
    return Math.min(after, Math.max(before, position));
  }
}
