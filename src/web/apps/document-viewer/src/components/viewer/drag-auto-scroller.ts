const EDGE_THRESHOLD_PX = 48;
const MAX_SCROLL_STEP_PX = 20;

/**
 * Returns the per-frame scroll distance for one viewport axis. Scrolling
 * accelerates as the pointer approaches (or moves beyond) a visible edge.
 */
export function getEdgeScrollStep(
  pointerPosition: number,
  viewportStart: number,
  viewportEnd: number,
  edgeThreshold = EDGE_THRESHOLD_PX,
  maxScrollStep = MAX_SCROLL_STEP_PX,
): number {
  if (edgeThreshold <= 0 || maxScrollStep <= 0 || viewportEnd <= viewportStart) return 0;

  const towardStart = Math.max(
    0,
    Math.min(1, (viewportStart + edgeThreshold - pointerPosition) / edgeThreshold),
  );
  const towardEnd = Math.max(
    0,
    Math.min(1, (pointerPosition - (viewportEnd - edgeThreshold)) / edgeThreshold),
  );

  return (towardEnd - towardStart) * maxScrollStep;
}

/** Prevents an edge-scroll step from moving beyond the dragged page. */
export function limitScrollStepToTarget(
  scrollStep: number,
  pointerPosition: number,
  targetStart: number,
  targetEnd: number,
): number {
  if (scrollStep < 0) {
    return Math.min(0, Math.max(scrollStep, targetStart - pointerPosition));
  }
  if (scrollStep > 0) {
    return Math.max(0, Math.min(scrollStep, targetEnd - pointerPosition));
  }
  return 0;
}

/** Auto-scrolls a viewer while a drag is held near or beyond its visible edges. */
export class DragAutoScroller {
  private readonly _scrollElement: HTMLElement;
  private _pointerX = 0;
  private _pointerY = 0;
  private _hasPointerUpdate = false;
  private _dragTarget: HTMLElement | null = null;
  private _animationFrame: number | null = null;
  private _onScroll: ((clientX: number, clientY: number) => void) | null = null;

  constructor(scrollElement: HTMLElement) {
    this._scrollElement = scrollElement;
  }

  start(
    clientX: number,
    clientY: number,
    dragTarget: HTMLElement,
    onScroll: (clientX: number, clientY: number) => void,
  ): void {
    this.stop();
    this._pointerX = clientX;
    this._pointerY = clientY;
    this._hasPointerUpdate = false;
    this._dragTarget = dragTarget;
    this._onScroll = onScroll;
    this._animationFrame = requestAnimationFrame(() => this._tick());
  }

  updatePointer(clientX: number, clientY: number): void {
    this._pointerX = clientX;
    this._pointerY = clientY;
    this._hasPointerUpdate = true;
  }

  stop(): void {
    if (this._animationFrame !== null) cancelAnimationFrame(this._animationFrame);
    this._animationFrame = null;
    this._hasPointerUpdate = false;
    this._dragTarget = null;
    this._onScroll = null;
  }

  private _tick(): void {
    if (!this._onScroll || !this._dragTarget) return;
    if (!this._hasPointerUpdate) {
      this._animationFrame = requestAnimationFrame(() => this._tick());
      return;
    }

    const bounds = this._scrollElement.getBoundingClientRect();
    const viewportRight = bounds.left + this._scrollElement.clientWidth;
    const viewportBottom = bounds.top + this._scrollElement.clientHeight;
    const targetBounds = this._dragTarget.getBoundingClientRect();
    const stepX = limitScrollStepToTarget(
      getEdgeScrollStep(this._pointerX, bounds.left, viewportRight),
      this._pointerX,
      targetBounds.left,
      targetBounds.right,
    );
    const stepY = limitScrollStepToTarget(
      getEdgeScrollStep(this._pointerY, bounds.top, viewportBottom),
      this._pointerY,
      targetBounds.top,
      targetBounds.bottom,
    );

    const previousLeft = this._scrollElement.scrollLeft;
    const previousTop = this._scrollElement.scrollTop;
    this._scrollElement.scrollLeft += stepX;
    this._scrollElement.scrollTop += stepY;

    if (
      this._scrollElement.scrollLeft !== previousLeft
      || this._scrollElement.scrollTop !== previousTop
    ) {
      this._onScroll(this._pointerX, this._pointerY);
    }

    this._animationFrame = requestAnimationFrame(() => this._tick());
  }
}
