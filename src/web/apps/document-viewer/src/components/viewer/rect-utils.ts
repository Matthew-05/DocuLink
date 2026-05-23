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

/**
 * Computes a new normalized rect after a resize drag from a corner.
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

  const startLeft   = startRect.x * pageW;
  const startTop    = startRect.y * pageH;
  const startRight  = (startRect.x + startRect.width) * pageW;
  const startBottom = (startRect.y + startRect.height) * pageH;

  let left = startLeft;
  let top = startTop;
  let right = startRight;
  let bottom = startBottom;

  switch (handle) {
    case "nw":
      left = curXPx;
      top = curYPx;
      break;
    case "ne":
      right = curXPx;
      top = curYPx;
      break;
    case "sw":
      left = curXPx;
      bottom = curYPx;
      break;
    case "se":
      right = curXPx;
      bottom = curYPx;
      break;
  }

  left   = clamp(left,   0, pageW);
  top    = clamp(top,    0, pageH);
  right  = clamp(right,  0, pageW);
  bottom = clamp(bottom, 0, pageH);

  if (right - left < MIN_DRAG_PX) {
    if (handle === "nw" || handle === "sw") left = right - MIN_DRAG_PX;
    else right = left + MIN_DRAG_PX;
  }
  if (bottom - top < MIN_DRAG_PX) {
    if (handle === "nw" || handle === "ne") top = bottom - MIN_DRAG_PX;
    else bottom = top + MIN_DRAG_PX;
  }

  left   = clamp(left,   0, pageW - MIN_DRAG_PX);
  top    = clamp(top,    0, pageH - MIN_DRAG_PX);
  right  = clamp(right,  left + MIN_DRAG_PX, pageW);
  bottom = clamp(bottom, top + MIN_DRAG_PX, pageH);

  return {
    x:      left / pageW,
    y:      top / pageH,
    width:  (right - left) / pageW,
    height: (bottom - top) / pageH,
  };
}
