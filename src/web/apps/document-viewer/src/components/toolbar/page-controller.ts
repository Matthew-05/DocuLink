import type { PageState } from "../../types/index.js";

export class PageController {
  readonly element: HTMLElement;

  private _state: PageState = { current: 1, total: 0 };
  private _input: HTMLInputElement;
  private _totalSpan: HTMLSpanElement;
  private readonly _callbacks: Array<(page: number) => void> = [];

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "page-controller toolbar__slot";

    this._input = document.createElement("input");
    this._input.className = "page-controller__input";
    this._input.type = "number";
    this._input.min = "1";
    this._input.value = "1";
    this._input.addEventListener("change", () => this._handleInputChange());
    this._input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") this._handleInputChange();
    });

    const separator = document.createElement("span");
    separator.className = "page-controller__separator";
    separator.textContent = "/";

    this._totalSpan = document.createElement("span");
    this._totalSpan.className = "page-controller__total";
    this._totalSpan.textContent = "0";

    this.element.append(this._input, separator, this._totalSpan);
  }

  onChange(cb: (page: number) => void): void {
    this._callbacks.push(cb);
  }

  setTotal(total: number): void {
    this._state = { ...this._state, total };
    this._input.max = String(total);
    this._totalSpan.textContent = String(total);
  }

  setCurrentPage(page: number): void {
    this._state = { ...this._state, current: page };
    this._input.value = String(page);
  }

  private _handleInputChange(): void {
    const raw = parseInt(this._input.value, 10);
    if (isNaN(raw)) {
      this._input.value = String(this._state.current);
      return;
    }
    const clamped = Math.min(this._state.total, Math.max(1, raw));
    this.setCurrentPage(clamped);
    for (const cb of this._callbacks) cb(clamped);
  }
}
