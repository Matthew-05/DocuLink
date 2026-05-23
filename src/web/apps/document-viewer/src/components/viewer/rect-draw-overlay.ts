import type { PdfViewer } from "./pdf-viewer.js";
import type { TextContentCache } from "../../services/text-content-cache.js";
import { extractText } from "../../services/text-extractor.js";
import type { LinkRectPayload } from "../../types/index.js";
import {
  clamp,
  MIN_DRAG_PX,
  pixelBoxToNormalizedRect,
} from "./rect-utils.js";

type RectCreatedCallback = (payload: LinkRectPayload) => void;

interface DragState {
  pageWrapper: HTMLDivElement;
  /** 0-based page index matching the storage schema. */
  pageIndex: number;
  /** Drag start position in page-wrapper pixel space. */
  startXPx: number;
  startYPx: number;
  selectionDiv: HTMLDivElement;
}

const LINK_SELECTOR = ".rect-draw__link";

/**
 * Attaches always-on click-drag rectangle selection to the PDF viewer.
 * No mode toggle is needed — drawing is active whenever a document is loaded.
 */
export class RectDrawOverlay {
  private _dragState: DragState | null = null;
  private readonly _onRectCreatedCallbacks: RectCreatedCallback[] = [];

  private readonly _boundMouseDown: (e: MouseEvent) => void;
  private readonly _boundMouseMove: (e: MouseEvent) => void;
  private readonly _boundMouseUp:   (e: MouseEvent) => void;

  constructor(
    private readonly _viewer: PdfViewer,
    private readonly _cache: TextContentCache,
  ) {
    this._boundMouseDown = this._onMouseDown.bind(this);
    this._boundMouseMove = this._onMouseMove.bind(this);
    this._boundMouseUp   = this._onMouseUp.bind(this);

    this._viewer.element.addEventListener("mousedown", this._boundMouseDown);
  }

  onRectCreated(cb: RectCreatedCallback): void {
    this._onRectCreatedCallbacks.push(cb);
  }

  private _onMouseDown(e: MouseEvent): void {
    if (e.button !== 0) return;
    if (!this._viewer.getDocument()) return;
    if (e.target instanceof Element && e.target.closest(LINK_SELECTOR)) return;

    const pageWrapper = this._findPageWrapper(e.target);
    if (!pageWrapper) return;

    e.preventDefault();

    const pageNum   = parseInt(pageWrapper.dataset["page"] ?? "0", 10);
    const pageIndex = pageNum - 1;

    const wrapperRect = pageWrapper.getBoundingClientRect();
    const startX = e.clientX - wrapperRect.left;
    const startY = e.clientY - wrapperRect.top;

    const selectionDiv = document.createElement("div");
    selectionDiv.className = "rect-draw__selection";
    selectionDiv.style.left   = `${startX}px`;
    selectionDiv.style.top    = `${startY}px`;
    selectionDiv.style.width  = "0px";
    selectionDiv.style.height = "0px";
    pageWrapper.appendChild(selectionDiv);

    this._dragState = { pageWrapper, pageIndex, startXPx: startX, startYPx: startY, selectionDiv };

    document.addEventListener("mousemove", this._boundMouseMove);
    document.addEventListener("mouseup",   this._boundMouseUp);
  }

  private _onMouseMove(e: MouseEvent): void {
    const state = this._dragState;
    if (!state) return;

    const { pageWrapper, startXPx, startYPx, selectionDiv } = state;
    const wrapperRect = pageWrapper.getBoundingClientRect();

    const curX = clamp(e.clientX - wrapperRect.left, 0, pageWrapper.offsetWidth);
    const curY = clamp(e.clientY - wrapperRect.top,  0, pageWrapper.offsetHeight);

    selectionDiv.style.left   = `${Math.min(startXPx, curX)}px`;
    selectionDiv.style.top    = `${Math.min(startYPx, curY)}px`;
    selectionDiv.style.width  = `${Math.abs(curX - startXPx)}px`;
    selectionDiv.style.height = `${Math.abs(curY - startYPx)}px`;
  }

  private _onMouseUp(e: MouseEvent): void {
    document.removeEventListener("mousemove", this._boundMouseMove);
    document.removeEventListener("mouseup",   this._boundMouseUp);

    const state = this._dragState;
    this._dragState = null;
    if (!state) return;

    const { pageWrapper, pageIndex, startXPx, startYPx, selectionDiv } = state;
    selectionDiv.remove();

    const wrapperRect = pageWrapper.getBoundingClientRect();
    const curX = clamp(e.clientX - wrapperRect.left, 0, pageWrapper.offsetWidth);
    const curY = clamp(e.clientY - wrapperRect.top,  0, pageWrapper.offsetHeight);

    const pxW = Math.abs(curX - startXPx);
    const pxH = Math.abs(curY - startYPx);
    if (pxW < MIN_DRAG_PX || pxH < MIN_DRAG_PX) return;

    const normalizedRect = pixelBoxToNormalizedRect(pageWrapper, startXPx, startYPx, curX, curY);

    const pdfId   = this._viewer.getActivePdfId() ?? "";
    const entries = this._cache.get(pdfId, pageIndex);
    const text    = extractText(entries, normalizedRect);

    for (const cb of this._onRectCreatedCallbacks) cb({ pdfId, page: pageIndex, rect: normalizedRect, text });
  }

  private _findPageWrapper(target: EventTarget | null): HTMLDivElement | null {
    if (!target || !(target instanceof Element)) return null;
    return target.closest<HTMLDivElement>("[data-page]");
  }
}
