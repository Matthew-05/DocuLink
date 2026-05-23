import type { PdfViewer } from "./pdf-viewer.js";
import type { TextContentCache } from "../../services/text-content-cache.js";
import type { RectRenderer } from "./rect-renderer.js";
import { extractText } from "../../services/text-extractor.js";
import type { LinkRectUpdatedPayload, NormalizedRect } from "../../types/index.js";
import {
  applyNormalizedRectToElement,
  clamp,
  cursorForResizeCorner,
  getLinkResizeCorner,
  MIN_DRAG_PX,
  resizeRectFromHandle,
  type ResizeHandle,
} from "./rect-utils.js";

type RectUpdatedCallback = (payload: LinkRectUpdatedPayload) => void;

interface EditDragState {
  id: string;
  pdfId: string;
  pageIndex: number;
  pageWrapper: HTMLDivElement;
  linkEl: HTMLDivElement;
  handle: ResizeHandle;
  startRect: NormalizedRect;
  startXPx: number;
  startYPx: number;
}

const LINK_CLASS = "rect-draw__link";

/**
 * Enables corner-only resize on persisted link rectangle overlays.
 * Cursor changes at corners indicate resize; the body remains click-to-navigate.
 */
export class RectEditOverlay {
  private _dragState: EditDragState | null = null;
  /** Set after a committed drag so the subsequent click is suppressed. */
  private _suppressNextClick = false;
  private _hoveredLink: HTMLDivElement | null = null;
  private readonly _onRectUpdatedCallbacks: RectUpdatedCallback[] = [];

  private readonly _boundMouseDown: (e: MouseEvent) => void;
  private readonly _boundMouseMove: (e: MouseEvent) => void;
  private readonly _boundMouseUp:   (e: MouseEvent) => void;
  private readonly _boundHoverMove: (e: MouseEvent) => void;
  private readonly _boundHoverLeave: (e: MouseEvent) => void;
  private readonly _boundClick: (e: MouseEvent) => void;

  constructor(
    private readonly _viewer: PdfViewer,
    private readonly _cache: TextContentCache,
    private readonly _renderer: RectRenderer,
  ) {
    this._boundMouseDown  = this._onMouseDown.bind(this);
    this._boundMouseMove  = this._onMouseMove.bind(this);
    this._boundMouseUp    = this._onMouseUp.bind(this);
    this._boundHoverMove  = this._onHoverMove.bind(this);
    this._boundHoverLeave = this._onHoverLeave.bind(this);
    this._boundClick      = this._onClick.bind(this);

    const el = this._viewer.element;
    el.addEventListener("mousedown", this._boundMouseDown,  true);
    el.addEventListener("mousemove", this._boundHoverMove);
    el.addEventListener("mouseleave", this._boundHoverLeave);
    el.addEventListener("click",     this._boundClick,     true);
  }

  onRectUpdated(cb: RectUpdatedCallback): void {
    this._onRectUpdatedCallbacks.push(cb);
  }

  /** Returns true when the next click on a link should be suppressed. */
  consumeClickSuppression(): boolean {
    if (!this._suppressNextClick) return false;
    this._suppressNextClick = false;
    return true;
  }

  private _linkUnderPointer(clientX: number, clientY: number): HTMLDivElement | null {
    const hit = document.elementFromPoint(clientX, clientY);
    if (!hit || !(hit instanceof Element)) return null;
    return hit.closest<HTMLDivElement>(`.${LINK_CLASS}`);
  }

  private _onHoverMove(e: MouseEvent): void {
    if (this._dragState) return;

    const linkEl = this._linkUnderPointer(e.clientX, e.clientY);

    if (this._hoveredLink && this._hoveredLink !== linkEl) {
      this._resetLinkCursor(this._hoveredLink);
      this._hoveredLink = null;
    }

    if (!linkEl) return;

    this._hoveredLink = linkEl;
    const corner = getLinkResizeCorner(linkEl, e.clientX, e.clientY);
    linkEl.style.cursor = cursorForResizeCorner(corner);
  }

  private _onHoverLeave(e: MouseEvent): void {
    if (this._dragState) return;

    const related = e.relatedTarget;
    if (related instanceof Element && related.closest(`.${LINK_CLASS}`)) return;

    if (this._hoveredLink) {
      this._resetLinkCursor(this._hoveredLink);
      this._hoveredLink = null;
    }
  }

  private _resetLinkCursor(linkEl: HTMLDivElement): void {
    linkEl.style.cursor = "";
  }

  private _onMouseDown(e: MouseEvent): void {
    if (e.button !== 0) return;

    const linkEl = this._linkUnderPointer(e.clientX, e.clientY);
    if (!linkEl) return;

    const handle = getLinkResizeCorner(linkEl, e.clientX, e.clientY);
    if (!handle) return;

    e.preventDefault();
    e.stopPropagation();

    const id = linkEl.dataset["rectId"];
    if (!id) return;

    const entry = this._renderer.getRectangle(id);
    if (!entry) return;

    const pageWrapper = linkEl.closest<HTMLDivElement>("[data-page]");
    if (!pageWrapper) return;

    const pageNum   = parseInt(pageWrapper.dataset["page"] ?? "0", 10);
    const pageIndex = pageNum - 1;

    const wrapperRect = pageWrapper.getBoundingClientRect();
    const startXPx = e.clientX - wrapperRect.left;
    const startYPx = e.clientY - wrapperRect.top;

    linkEl.classList.add("rect-draw__link--editing");
    linkEl.style.cursor = cursorForResizeCorner(handle);

    this._dragState = {
      id,
      pdfId: entry.pdfId,
      pageIndex,
      pageWrapper,
      linkEl,
      handle,
      startRect: { ...entry.rect },
      startXPx,
      startYPx,
    };

    document.addEventListener("mousemove", this._boundMouseMove);
    document.addEventListener("mouseup",   this._boundMouseUp);
  }

  private _onMouseMove(e: MouseEvent): void {
    const state = this._dragState;
    if (!state) return;

    const { pageWrapper, startRect, linkEl, handle } = state;
    const wrapperRect = pageWrapper.getBoundingClientRect();
    const curXPx = clamp(e.clientX - wrapperRect.left, 0, pageWrapper.offsetWidth);
    const curYPx = clamp(e.clientY - wrapperRect.top,  0, pageWrapper.offsetHeight);

    const newRect = resizeRectFromHandle(pageWrapper, startRect, handle, curXPx, curYPx);
    applyNormalizedRectToElement(linkEl, newRect);
  }

  private _onMouseUp(e: MouseEvent): void {
    document.removeEventListener("mousemove", this._boundMouseMove);
    document.removeEventListener("mouseup",   this._boundMouseUp);

    const state = this._dragState;
    this._dragState = null;
    if (!state) return;

    const { id, pdfId, pageIndex, pageWrapper, linkEl, handle, startRect, startXPx, startYPx } = state;

    linkEl.classList.remove("rect-draw__link--editing");

    const wrapperRect = pageWrapper.getBoundingClientRect();
    const curXPx = clamp(e.clientX - wrapperRect.left, 0, pageWrapper.offsetWidth);
    const curYPx = clamp(e.clientY - wrapperRect.top,  0, pageWrapper.offsetHeight);

    const movedPx = Math.max(Math.abs(curXPx - startXPx), Math.abs(curYPx - startYPx));
    if (movedPx < MIN_DRAG_PX) {
      applyNormalizedRectToElement(linkEl, startRect);
      const corner = getLinkResizeCorner(linkEl, e.clientX, e.clientY);
      linkEl.style.cursor = cursorForResizeCorner(corner);
      return;
    }

    const finalRect = resizeRectFromHandle(pageWrapper, startRect, handle, curXPx, curYPx);

    applyNormalizedRectToElement(linkEl, finalRect);
    this._renderer.updateRectangle(id, finalRect);

    const entries = this._cache.get(pdfId, pageIndex);
    const text    = extractText(entries, finalRect);

    this._suppressNextClick = true;
    for (const cb of this._onRectUpdatedCallbacks) {
      cb({ id, pdfId, page: pageIndex, rect: finalRect, text });
    }

    this._resetLinkCursor(linkEl);
  }

  private _onClick(e: MouseEvent): void {
    if (!this._suppressNextClick) return;
    const linkEl = e.target instanceof Element
      ? e.target.closest(`.${LINK_CLASS}`)
      : null;
    if (!linkEl) return;
    e.stopImmediatePropagation();
    this._suppressNextClick = false;
  }
}
