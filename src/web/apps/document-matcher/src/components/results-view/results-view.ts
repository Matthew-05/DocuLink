import type { RowResult } from "../../types/index.js";

type Phase = "matching" | "creating" | "done" | "error";

export interface ResultsViewCallbacks {
  onClose: () => void;
}

export class ResultsView {
  private readonly _el: HTMLElement;
  private _progressBar!: HTMLElement;
  private _progressLabel!: HTMLElement;
  private _log!: HTMLElement;
  private _statusLine!: HTMLElement;
  private _closeBtn!: HTMLButtonElement;
  private _total: number;
  private _processed = 0;

  constructor(container: HTMLElement, total: number, callbacks: ResultsViewCallbacks) {
    this._total = total;
    this._el = document.createElement("div");
    this._el.className = "results-view";
    container.appendChild(this._el);
    this._build(callbacks);
  }

  private _build(callbacks: ResultsViewCallbacks): void {
    const header = document.createElement("div");
    header.className = "results-view__header";

    this._statusLine = document.createElement("span");
    this._statusLine.className = "results-view__status";
    this._statusLine.textContent = "Matching…";
    header.appendChild(this._statusLine);

    this._el.appendChild(header);

    const progressWrap = document.createElement("div");
    progressWrap.className = "results-view__progress-wrap";

    this._progressBar = document.createElement("div");
    this._progressBar.className = "results-view__progress-bar";
    const fill = document.createElement("div");
    fill.className = "results-view__progress-fill";
    this._progressBar.appendChild(fill);
    progressWrap.appendChild(this._progressBar);

    this._progressLabel = document.createElement("span");
    this._progressLabel.className = "results-view__progress-label";
    this._progressLabel.textContent = `0 / ${this._total} rows`;
    progressWrap.appendChild(this._progressLabel);

    this._el.appendChild(progressWrap);

    this._log = document.createElement("div");
    this._log.className = "results-view__log";
    this._el.appendChild(this._log);

    const footer = document.createElement("div");
    footer.className = "results-view__footer";

    this._closeBtn = document.createElement("button");
    this._closeBtn.className = "btn btn--primary";
    this._closeBtn.textContent = "Close";
    this._closeBtn.disabled = true;
    this._closeBtn.addEventListener("click", () => callbacks.onClose());
    footer.appendChild(this._closeBtn);

    this._el.appendChild(footer);
  }

  addRowResult(result: RowResult): void {
    this._processed++;
    const fill = this._progressBar.querySelector<HTMLElement>(".results-view__progress-fill")!;
    const pct = this._total > 0 ? (this._processed / this._total) * 100 : 0;
    fill.style.width = `${pct}%`;
    this._progressLabel.textContent = `${this._processed} / ${this._total} rows`;

    if (result.status === "skipped") return;

    const entry = document.createElement("div");
    entry.className = `results-view__log-entry results-view__log-entry--${result.status}`;

    const icon = result.status === "matched" ? "✓" : "✗";
    const label =
      result.status === "matched"
        ? `Row ${result.rowIndex + 1}  →  ${result.pdfName ?? ""}`
        : `Row ${result.rowIndex + 1}  →  No match found`;

    entry.innerHTML = `<span class="results-view__icon">${icon}</span><span>${label}</span>`;
    this._log.appendChild(entry);
    this._log.scrollTop = this._log.scrollHeight;
  }

  setPhase(phase: Phase, summary?: { matched: number; unmatched: number }): void {
    switch (phase) {
      case "creating":
        this._statusLine.textContent = "Creating links in Excel…";
        break;
      case "error":
        this._statusLine.textContent = "Matching failed";
        this._closeBtn.disabled = false;
        break;
      case "done":
        this._statusLine.textContent = summary
          ? `Complete: ${summary.matched} matched · ${summary.unmatched} unmatched`
          : "Complete";
        this._closeBtn.disabled = false;
        break;
    }
  }

  get element(): HTMLElement {
    return this._el;
  }

  remove(): void {
    this._el.remove();
  }
}
