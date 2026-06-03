import type { SelectionInfo } from "../../types/index.js";

export interface StepSelectRangesCallbacks {
  onNext: () => void;
}

export class StepSelectRanges {
  private readonly _el: HTMLElement;
  private _rangeDisplayEl!: HTMLElement;
  private _keyListEl!: HTMLElement;
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
      "Select one or more column ranges in Excel — each selected range becomes a key column for matching. Hold Ctrl to select multiple ranges.";
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

    this._keyListEl = document.createElement("ul");
    this._keyListEl.className = "step-select-ranges__key-list";
    body.appendChild(this._keyListEl);

    this._el.appendChild(body);

    const footer = document.createElement("div");
    footer.className = "wizard-step__footer";

    this._nextBtn = document.createElement("button");
    this._nextBtn.className = "btn btn--primary";
    this._nextBtn.textContent = "Next →";
    this._nextBtn.addEventListener("click", () => callbacks.onNext());
    footer.appendChild(this._nextBtn);

    this._el.appendChild(footer);
  }

  update(info: SelectionInfo): void {
    this._update(info);
  }

  private _update(info: SelectionInfo): void {
    this._rangeDisplayEl.textContent = info.rangeDisplay || "No selection";
    const count = info.keyColumns.length;
    const dataRows = Math.max(0, info.rowCount - 1);
    this._subtitleEl.textContent =
      count > 0
        ? `${count} key column${count !== 1 ? "s" : ""} · ${dataRows} data row${dataRows !== 1 ? "s" : ""}`
        : "Nothing selected";

    this._keyListEl.innerHTML = "";
    for (const col of info.keyColumns) {
      const li = document.createElement("li");
      li.className = "step-select-ranges__key-item";
      li.innerHTML = `<span class="step-select-ranges__key-header">${escHtml(col.header)}</span><span class="step-select-ranges__key-addr">${escHtml(col.rangeAddress)}</span>`;
      this._keyListEl.appendChild(li);
    }

    this._nextBtn.disabled = count === 0;
  }

  get element(): HTMLElement {
    return this._el;
  }

  remove(): void {
    this._el.remove();
  }
}

function escHtml(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
