import { sendClearOutputRangePreview, sendPreviewOutputRange } from "../../host-bridge.js";
import type { KeyColumnInfo, OutputColumnInfo } from "../../types/index.js";
import { ColumnSelect } from "../column-select/column-select.js";

/**
 * Hover moves faster than Excel can select, so previews are coalesced: only the
 * column the pointer settled on is sent.
 */
const PREVIEW_DEBOUNCE_MS = 40;

export interface StepOutputColumnsCallbacks {
  onBack: () => void;
  onNext: (outputColNumbers: number[]) => void;
}

export class StepOutputColumns {
  private readonly _el: HTMLElement;
  private _keyColumns: KeyColumnInfo[];
  private _outputColumns: OutputColumnInfo[];
  private _outputColNumbers: number[];
  private _pairsContainer!: HTMLElement;
  private _nextBtn!: HTMLButtonElement;
  private _backBtn!: HTMLButtonElement;
  private _errorEl!: HTMLElement;
  private _selects: ColumnSelect[] = [];
  private _callbacks: StepOutputColumnsCallbacks;
  private _previewTimer: number | null = null;

  constructor(
    container: HTMLElement,
    keyColumns: KeyColumnInfo[],
    outputColumns: OutputColumnInfo[],
    callbacks: StepOutputColumnsCallbacks,
  ) {
    this._keyColumns = keyColumns;
    this._outputColumns = outputColumns;
    this._outputColNumbers = keyColumns.map((_, i) => outputColumns[i]?.colNumber ?? outputColumns[0]?.colNumber ?? 0);
    this._callbacks = callbacks;

    this._el = document.createElement("div");
    this._el.className = "wizard-step step-output-columns";
    container.appendChild(this._el);
    this._build();
  }

  /** Call when selection changed while on a later step — refreshes pairs. */
  refresh(keyColumns: KeyColumnInfo[], outputColumns: OutputColumnInfo[]): void {
    this._keyColumns = keyColumns;
    this._outputColumns = outputColumns;
    this._outputColNumbers = keyColumns.map((_, i) => outputColumns[i]?.colNumber ?? outputColumns[0]?.colNumber ?? 0);
    this._renderPairs();
    this._validate();
  }

  private _build(): void {
    const body = document.createElement("div");
    body.className = "wizard-step__body";

    const instructions = document.createElement("p");
    instructions.className = "step-output-columns__instructions";
    instructions.textContent =
      "For each key column, choose which column should receive the matched link.";
    body.appendChild(instructions);

    if (this._outputColumns.length === 0) {
      const empty = document.createElement("p");
      empty.className = "config-view__empty";
      empty.textContent =
        "No output columns available — the selected key columns span to the last used column. Go back and adjust your selection.";
      body.appendChild(empty);
    } else {
      const pairsHeader = document.createElement("div");
      pairsHeader.className = "config-view__pairs-header";
      pairsHeader.innerHTML = "<span>Key Column</span><span></span><span>Output Column</span>";
      body.appendChild(pairsHeader);

      this._pairsContainer = document.createElement("div");
      this._pairsContainer.className = "config-view__pairs";
      body.appendChild(this._pairsContainer);

      this._errorEl = document.createElement("p");
      this._errorEl.className = "step-output-columns__error";
      this._errorEl.textContent = "Each key column must map to a unique output column.";
      this._errorEl.hidden = true;
      body.appendChild(this._errorEl);

      this._renderPairs();
    }

    this._el.appendChild(body);

    const footer = document.createElement("div");
    footer.className = "wizard-step__footer";

    this._backBtn = document.createElement("button");
    this._backBtn.className = "btn btn--ghost";
    this._backBtn.textContent = "← Back";
    this._backBtn.addEventListener("click", () => this._callbacks.onBack());

    this._nextBtn = document.createElement("button");
    this._nextBtn.className = "btn btn--primary";
    this._nextBtn.textContent = "Next →";
    this._nextBtn.addEventListener("click", () =>
      this._callbacks.onNext([...this._outputColNumbers]),
    );

    footer.appendChild(this._backBtn);
    footer.appendChild(this._nextBtn);
    this._el.appendChild(footer);

    this._validate();
  }

  private _renderPairs(): void {
    if (!this._pairsContainer) return;
    this._cancelPendingPreview();
    for (const select of this._selects) select.remove();
    this._pairsContainer.innerHTML = "";
    this._selects = [];

    const options = this._outputColumns.map((col) => ({
      value: col.colNumber,
      label: col.header,
    }));

    this._keyColumns.forEach((keyCol, i) => {
      const row = document.createElement("div");
      row.className = "config-view__pair-row";

      const keyLabel = document.createElement("div");
      keyLabel.className = "config-view__key-label";
      keyLabel.title = keyCol.rangeAddress;
      keyLabel.textContent = colNumberToLetter(keyCol.colNumber);

      const arrow = document.createElement("span");
      arrow.className = "config-view__arrow";
      arrow.textContent = "→";

      const sel = new ColumnSelect(options, this._outputColNumbers[i] ?? 0, {
        onChange: (value) => {
          this._outputColNumbers[i] = value;
          this._validate();
        },
        onOptionHover: (value) => this._schedulePreview(value),
        onHoverEnd: () => this._clearPreview(),
      });

      this._selects.push(sel);
      row.appendChild(keyLabel);
      row.appendChild(arrow);
      row.appendChild(sel.element);
      this._pairsContainer.appendChild(row);
    });

    this._validate();
  }

  private _validate(): void {
    const noOptions = this._keyColumns.length === 0 || this._outputColumns.length === 0;

    const seen = new Set<number>();
    const duplicates = new Set<number>();
    for (const n of this._outputColNumbers) {
      if (seen.has(n)) duplicates.add(n);
      else seen.add(n);
    }
    const hasDuplicates = duplicates.size > 0;

    this._selects.forEach((sel, i) => {
      sel.setError(duplicates.has(this._outputColNumbers[i] ?? -1));
    });

    if (this._errorEl) this._errorEl.hidden = !hasDuplicates;

    if (this._nextBtn) {
      this._nextBtn.disabled = noOptions || hasDuplicates;
    }
  }

  private _schedulePreview(colNumber: number): void {
    this._cancelPendingPreview();
    this._previewTimer = window.setTimeout(() => {
      this._previewTimer = null;
      sendPreviewOutputRange(colNumber);
    }, PREVIEW_DEBOUNCE_MS);
  }

  private _clearPreview(): void {
    this._cancelPendingPreview();
    sendClearOutputRangePreview();
  }

  private _cancelPendingPreview(): void {
    if (this._previewTimer === null) return;
    window.clearTimeout(this._previewTimer);
    this._previewTimer = null;
  }

  get element(): HTMLElement {
    return this._el;
  }

  remove(): void {
    // Leaving the step must put the user's selection back, whichever way they left.
    this._clearPreview();
    for (const select of this._selects) select.remove();
    this._selects = [];
    this._el.remove();
  }
}

function colNumberToLetter(colNumber: number): string {
  let n = colNumber;
  let result = "";
  while (n > 0) {
    const rem = (n - 1) % 26;
    result = String.fromCharCode(65 + rem) + result;
    n = Math.floor((n - 1) / 26);
  }
  return result || String(colNumber);
}
