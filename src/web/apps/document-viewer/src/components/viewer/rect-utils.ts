import type { NormalizedRect } from "../../types/index.js";

/** Minimum drag size (px) required to commit a selection or edit. */
export const MIN_DRAG_PX = 4;

export function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}

/**
 * Converts a pixel-space drag box (relative to a page wrapper) into a
 * normalized rectangle clamped to the page bounds.
 */
export function pixelBoxToNormalizedRect(
  pageWrapper: HTMLElement,
  x1: number,
  y1: number,
  x2: number,
  y2: number,
): NormalizedRect {
  const pageW = pageWrapper.offsetWidth;
  const pageH = pageWrapper.offsetHeight;
  if (pageW <= 0 || pageH <= 0) {
    return { x: 0, y: 0, width: 0, height: 0 };
  }

  const left   = clamp(Math.min(x1, x2), 0, pageW);
  const top    = clamp(Math.min(y1, y2), 0, pageH);
  const right  = clamp(Math.max(x1, x2), 0, pageW);
  const bottom = clamp(Math.max(y1, y2), 0, pageH);

  return {
    x:      left / pageW,
    y:      top / pageH,
    width:  (right - left) / pageW,
    height: (bottom - top) / pageH,
  };
}

/** Applies normalized rect coordinates as CSS percentage positioning. */
export function applyNormalizedRectToElement(
  el: HTMLElement,
  rect: NormalizedRect,
): void {
  el.style.left   = `${rect.x      * 100}%`;
  el.style.top    = `${rect.y      * 100}%`;
  el.style.width  = `${rect.width  * 100}%`;
  el.style.height = `${rect.height * 100}%`;
}

export type ResizeHandle = "nw" | "ne" | "sw" | "se";

/** Pointer distance from a link corner that counts as a resize hit zone. */
export const CORNER_HIT_PX = 8;

/**
 * Returns the corner under the pointer, or null when not near a corner
 * (body clicks are for navigation only).
 */
export function getLinkResizeCorner(
  linkEl: HTMLElement,
  clientX: number,
  clientY: number,
): ResizeHandle | null {
  const rect = linkEl.getBoundingClientRect();
  const x = clientX - rect.left;
  const y = clientY - rect.top;
  const w = rect.width;
  const h = rect.height;
  const t = CORNER_HIT_PX;

  const nearLeft   = x <= t;
  const nearRight  = x >= w - t;
  const nearTop    = y <= t;
  const nearBottom = y >= h - t;

  if (nearTop && nearLeft) return "nw";
  if (nearTop && nearRight) return "ne";
  if (nearBottom && nearLeft) return "sw";
  if (nearBottom && nearRight) return "se";
  return null;
}

/**
 * Class that holds a resize cursor on the whole document for the length of a
 * drag. A hover cursor set on the rectangle stops applying the moment the
 * pointer leaves it, which a shrinking rectangle causes constantly.
 */
export function resizeDragCursorClass(corner: ResizeHandle): string {
  switch (corner) {
    case "nw":
    case "se":
      return "rect-draw--resizing-nwse";
    case "ne":
    case "sw":
      return "rect-draw--resizing-nesw";
  }
}

export function cursorForResizeCorner(corner: ResizeHandle | null): string {
  if (corner === null) return "pointer";
  switch (corner) {
    case "nw":
    case "se":
      return "nwse-resize";
    case "ne":
    case "sw":
      return "nesw-resize";
  }
}

/** Pixel-space corner that stays put while the opposite corner is dragged. */
interface ResizeAnchorPx {
  x: number;
  y: number;
}

/**
 * The corner diagonally opposite the dragged handle. It is fixed for the whole
 * drag, which is what lets the pointer travel past it and flip the rectangle.
 */
function resizeAnchorPx(
  startRect: NormalizedRect,
  handle: ResizeHandle,
  pageW: number,
  pageH: number,
): ResizeAnchorPx {
  const holdsWest  = handle === "nw" || handle === "sw";
  const holdsNorth = handle === "nw" || handle === "ne";
  return {
    x: (holdsWest  ? startRect.x + startRect.width  : startRect.x) * pageW,
    y: (holdsNorth ? startRect.y + startRect.height : startRect.y) * pageH,
  };
}

/**
 * Orders an anchored edge pair and keeps at least MIN_DRAG_PX between them.
 * The anchor never moves, so the minimum is taken out of the free edge, pushed
 * away from the anchor on whichever side the pointer is on.
 */
function spanFromAnchor(
  anchor: number,
  pointer: number,
  extent: number,
): { min: number; max: number } {
  let free = clamp(pointer, 0, extent);

  if (Math.abs(free - anchor) < MIN_DRAG_PX) {
    // A pointer sitting on the anchor has no side yet; grow whichever way fits.
    const towardEnd = free > anchor
      || (free === anchor && anchor + MIN_DRAG_PX <= extent);
    free = clamp(towardEnd ? anchor + MIN_DRAG_PX : anchor - MIN_DRAG_PX, 0, extent);
  }

  return free < anchor
    ? { min: free,   max: anchor }
    : { min: anchor, max: free };
}

/**
 * Computes a new normalized rect after a resize drag from a corner.
 *
 * Corners pass through each other: dragging one past its opposite flips the
 * rectangle rather than pinning it, and the result is always normalized with
 * positive width and height.
 */
export function resizeRectFromHandle(
  pageWrapper: HTMLElement,
  startRect: NormalizedRect,
  handle: ResizeHandle,
  curXPx: number,
  curYPx: number,
): NormalizedRect {
  const pageW = pageWrapper.offsetWidth;
  const pageH = pageWrapper.offsetHeight;
  if (pageW <= 0 || pageH <= 0) return startRect;

  const anchor = resizeAnchorPx(startRect, handle, pageW, pageH);
  const horizontal = spanFromAnchor(anchor.x, curXPx, pageW);
  const vertical   = spanFromAnchor(anchor.y, curYPx, pageH);

  return {
    x:      horizontal.min / pageW,
    y:      vertical.min   / pageH,
    width:  (horizontal.max - horizontal.min) / pageW,
    height: (vertical.max   - vertical.min)   / pageH,
  };
}

/**
 * The corner the pointer is holding now, which is the handle it grabbed
 * mirrored across each axis it has since crossed. Used to keep the resize
 * cursor pointing along the live diagonal mid-flip.
 */
export function resizeHandleFromDrag(
  pageWrapper: HTMLElement,
  startRect: NormalizedRect,
  handle: ResizeHandle,
  curXPx: number,
  curYPx: number,
): ResizeHandle {
  const pageW = pageWrapper.offsetWidth;
  const pageH = pageWrapper.offsetHeight;
  if (pageW <= 0 || pageH <= 0) return handle;

  const anchor = resizeAnchorPx(startRect, handle, pageW, pageH);
  const west  = clamp(curXPx, 0, pageW) < anchor.x ? "w" : "e";
  const north = clamp(curYPx, 0, pageH) < anchor.y ? "n" : "s";
  return `${north}${west}` as ResizeHandle;
}
