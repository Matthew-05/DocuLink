import type { ZoomLevel } from "../../types/index.js";

const MIN_SCALE = 0.25;
const MAX_SCALE = 4.0;
const STEP = 0.25;
const SCROLL_STEP = 0.1;

export interface ZoomAnchor {
  x: number;
  y: number;
}

export class ZoomController {
  static readonly SCROLL_STEP = SCROLL_STEP;

  readonly element: HTMLElement;

  private _scale: ZoomLevel = 1.0;
  private _label: HTMLSpanElement;
  private readonly _callbacks: Array<(scale: ZoomLevel, anchor?: ZoomAnchor) => void> = [];

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "zoom-controller toolbar__slot";

    const decreaseBtn = document.createElement("button");
    decreaseBtn.className = "zoom-controller__btn";
    decreaseBtn.title = "Zoom out";
    decreaseBtn.textContent = "−";
    decreaseBtn.addEventListener("click", () => this.adjustBy(-STEP));

    this._label = document.createElement("span");
    this._label.className = "zoom-controller__label";

    const increaseBtn = document.createElement("button");
    increaseBtn.className = "zoom-controller__btn";
    increaseBtn.title = "Zoom in";
    increaseBtn.textContent = "+";
    increaseBtn.addEventListener("click", () => this.adjustBy(+STEP));

    this.element.append(decreaseBtn, this._label, increaseBtn);
    this._updateLabel();
  }

  onChange(cb: (scale: ZoomLevel, anchor?: ZoomAnchor) => void): void {
    this._callbacks.push(cb);
  }

  setScale(scale: ZoomLevel): void {
    this._scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));
    this._updateLabel();
  }

  adjustBy(delta: number, anchor?: ZoomAnchor): void {
    const next = Math.round((this._scale + delta) * 100) / 100;
    const clamped = Math.min(MAX_SCALE, Math.max(MIN_SCALE, next));
    if (clamped === this._scale) return;

    this._scale = clamped;
    this._updateLabel();
    for (const cb of this._callbacks) cb(this._scale, anchor);
  }

  private _updateLabel(): void {
    this._label.textContent = `${Math.round(this._scale * 100)}%`;
  }
}
