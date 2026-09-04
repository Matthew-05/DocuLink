/** What the open document has to offer, as far as table detection knows. */
export interface TableToggleState {
  /** Whether a table model was stored for this document at all. */
  detected: boolean;
  /** Tables in the model. */
  total: number;
  /** Tables not yet covered by a link rectangle — what toggling would reveal. */
  remaining: number;
}

/**
 * The standing switch for detected-table suggestions.
 *
 * The notice on the page is a one-off announcement and can be dismissed for
 * good; this stays put, so the suggestions are always one click away with
 * nothing sitting on top of the document. It is present for every document,
 * including ones detection has never seen, because "nothing found here" and
 * "never looked here" are both answers worth being able to ask for — the
 * tooltip says which.
 */
export class TableToggle {
  readonly element: HTMLButtonElement;

  private readonly _badge: HTMLSpanElement;
  private readonly _callbacks: Array<(active: boolean) => void> = [];
  private _state: TableToggleState = { detected: false, total: 0, remaining: 0 };
  private _active = false;

  constructor() {
    this.element = document.createElement("button");
    this.element.type = "button";
    this.element.className = "table-toggle";
    this.element.setAttribute("aria-pressed", "false");

    const icon = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    icon.setAttribute("class", "table-toggle__icon");
    icon.setAttribute("viewBox", "0 0 16 16");
    icon.setAttribute("aria-hidden", "true");
    for (const [x, y, width, height] of [
      [1.5, 2.5, 13, 3],
      [1.5, 6.5, 5.5, 7],
      [8.5, 6.5, 6, 7],
    ] as const) {
      const cell = document.createElementNS("http://www.w3.org/2000/svg", "rect");
      cell.setAttribute("x", String(x));
      cell.setAttribute("y", String(y));
      cell.setAttribute("width", String(width));
      cell.setAttribute("height", String(height));
      cell.setAttribute("rx", "1");
      icon.appendChild(cell);
    }

    this._badge = document.createElement("span");
    this._badge.className = "table-toggle__badge";
    this._badge.hidden = true;
    this._badge.setAttribute("aria-hidden", "true");

    this.element.append(icon, this._badge);
    this.element.addEventListener("click", () => {
      this._active = !this._active;
      this._render();
      for (const callback of this._callbacks) callback(this._active);
    });
    this._render();
  }

  onToggle(callback: (active: boolean) => void): void {
    this._callbacks.push(callback);
  }

  setState(state: TableToggleState): void {
    this._state = {
      detected: state.detected,
      total: Math.max(0, state.total),
      remaining: Math.max(0, state.remaining),
    };
    this._render();
  }

  setActive(active: boolean): void {
    if (this._active === active) return;
    this._active = active;
    this._render();
  }

  private _render(): void {
    const { remaining } = this._state;

    // The badge counts what is left to suggest, so it empties out as the user
    // works rather than repeating a number that no longer means anything.
    this._badge.hidden = remaining === 0;
    this._badge.textContent = remaining > 99 ? "99+" : String(remaining);

    // Nothing to reveal is not an error, so the button stays put and simply
    // stops inviting a click.
    this.element.disabled = remaining === 0;
    this.element.classList.toggle("table-toggle--active", this._active && remaining > 0);
    this.element.setAttribute("aria-pressed", this._active && remaining > 0 ? "true" : "false");
    this.element.title = this._title();
    this.element.setAttribute("aria-label", this._title());
  }

  private _title(): string {
    const { detected, total, remaining } = this._state;
    if (!detected) return "Detected tables — run OCR on this document to detect tables";
    if (total === 0) return "Detected tables — none found in this document";
    if (remaining === 0) {
      return total === 1
        ? "Detected tables — the one found is already linked"
        : `Detected tables — all ${total} found are already linked`;
    }
    const subject = remaining === 1 ? "1 detected table" : `${remaining} detected tables`;
    return this._active ? `Hide ${subject}` : `Show ${subject}`;
  }
}
