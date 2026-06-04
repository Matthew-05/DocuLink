import type { SelectionInfo } from "../../types/index.js";

export interface StepSelectRangesCallbacks {
  onNext: () => void;
  onCancel: () => void;
}

export class StepSelectRanges {
  private readonly _el: HTMLElement;
  private _rangeDisplayEl!: HTMLElement;
  private _subtitleEl!: HTMLElement;
  private _nextBtn!: HTMLButtonElement;

  constructor(container: HTMLElement, initial: SelectionInfo, callbacks: StepSelectRangesCallbacks) {
    this._el = document.createElement("div");
    this._el.className = "wizard-step step-select-ranges";
    container.appendChild(this._el);
    this._build(callbacks);
    this._update(initial);
  }

  private _build(callbacks: StepSelectRangesCallbacks): void {
    const body = document.createElement("div");
    body.className = "wizard-step__body";

    const instructions = document.createElement("p");
    instructions.className = "step-select-ranges__instructions";
    instructions.textContent =
      "Select one or more column ranges in Excel. Each selected column becomes a key column, and every selected row is matched. Hold Ctrl to select multiple ranges.";
    body.appendChild(instructions);

    const displayWrap = document.createElement("div");
    displayWrap.className = "step-select-ranges__display-wrap";

    this._rangeDisplayEl = document.createElement("div");
    this._rangeDisplayEl.className = "step-select-ranges__range-display";
    displayWrap.appendChild(this._rangeDisplayEl);

    this._subtitleEl = document.createElement("div");
    this._subtitleEl.className = "step-select-ranges__subtitle";
    displayWrap.appendChild(this._subtitleEl);

    body.appendChild(displayWrap);

    this._el.appendChild(body);

    const footer = document.createElement("div");
    footer.className = "wizard-step__footer";

    const cancelBtn = document.createElement("button");
    cancelBtn.className = "btn btn--ghost";
    cancelBtn.textContent = "Cancel";
    cancelBtn.addEventListener("click", () => callbacks.onCancel());

    this._nextBtn = document.createElement("button");
    this._nextBtn.className = "btn btn--primary";
    this._nextBtn.textContent = "Next";
    this._nextBtn.addEventListener("click", () => callbacks.onNext());

    footer.appendChild(cancelBtn);
    footer.appendChild(this._nextBtn);
    this._el.appendChild(footer);
  }

  update(info: SelectionInfo): void {
    this._update(info);
  }

  private _update(info: SelectionInfo): void {
    const display = info.keyColumns.map((kc) => kc.rangeAddress.replace(/\$/g, "")).join(", ");
    this._rangeDisplayEl.textContent = display || "No selection";

    const count = info.keyColumns.length;
    const selectedRows = Math.max(0, info.rowCount);
    this._subtitleEl.textContent =
      count > 0
        ? `${count} key column${count !== 1 ? "s" : ""} - ${selectedRows} data row${selectedRows !== 1 ? "s" : ""}`
        : "Nothing selected";

    this._nextBtn.disabled = count === 0 || selectedRows === 0;
  }

  get element(): HTMLElement {
    return this._el;
  }

  remove(): void {
    this._el.remove();
  }
}
