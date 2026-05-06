import type { ZoomLevel } from "../../types/index.js";

const MIN_SCALE = 0.25;
const MAX_SCALE = 4.0;
const STEP = 0.25;

export class ZoomController {
  readonly element: HTMLElement;

  private _scale: ZoomLevel = 1.0;
  private _label: HTMLSpanElement;
  private readonly _callbacks: Array<(scale: ZoomLevel) => void> = [];

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "zoom-controller toolbar__slot";

    const decreaseBtn = document.createElement("button");
    decreaseBtn.className = "zoom-controller__btn";
    decreaseBtn.title = "Zoom out";
    decreaseBtn.textContent = "−";
    decreaseBtn.addEventListener("click", () => this._adjust(-STEP));

    this._label = document.createElement("span");
    this._label.className = "zoom-controller__label";

    const increaseBtn = document.createElement("button");
    increaseBtn.className = "zoom-controller__btn";
    increaseBtn.title = "Zoom in";
    increaseBtn.textContent = "+";
    increaseBtn.addEventListener("click", () => this._adjust(+STEP));

    this.element.append(decreaseBtn, this._label, increaseBtn);
    this._updateLabel();
  }

  onChange(cb: (scale: ZoomLevel) => void): void {
    this._callbacks.push(cb);
  }

  setScale(scale: ZoomLevel): void {
    this._scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));
    this._updateLabel();
  }

  private _adjust(delta: number): void {
    this.setScale(Math.round((this._scale + delta) * 100) / 100);
    for (const cb of this._callbacks) cb(this._scale);
  }

  private _updateLabel(): void {
    this._label.textContent = `${Math.round(this._scale * 100)}%`;
  }
}
