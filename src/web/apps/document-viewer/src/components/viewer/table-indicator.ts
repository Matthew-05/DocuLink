/**
 * Floating count of the tables detected in the open document, and the switch
 * that shows them.
 *
 * The count is what makes the control worth having: it answers "did detection
 * find anything here?" without the user turning anything on. A document with no
 * detected tables has nothing to say, so the control hides itself rather than
 * sitting there reading zero — on a document that was never OCR'd, a zero would
 * look like a detection failure rather than an absence of input.
 */
export class TableIndicator {
  readonly element: HTMLButtonElement;

  private readonly _label: HTMLSpanElement;
  private readonly _callbacks: Array<(active: boolean) => void> = [];
  private _count = 0;
  private _active = false;

  constructor() {
    this.element = document.createElement("button");
    this.element.type = "button";
    this.element.className = "table-indicator";
    this.element.hidden = true;
    this.element.setAttribute("aria-pressed", "false");

    const icon = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    icon.setAttribute("class", "table-indicator__icon");
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

    this._label = document.createElement("span");
    this._label.className = "table-indicator__label";

    this.element.append(icon, this._label);
    this.element.addEventListener("click", () => this._toggle());
    this._render();
  }

  onToggle(callback: (active: boolean) => void): void {
    this._callbacks.push(callback);
  }

  /** Tables detected across the whole document. Zero hides the control. */
  setCount(count: number): void {
    this._count = Math.max(0, count);
    this._render();
  }

  setActive(active: boolean): void {
    if (this._active === active) return;
    this._active = active;
    this._render();
  }

  private _toggle(): void {
    this._active = !this._active;
    this._render();
    for (const callback of this._callbacks) callback(this._active);
  }

  private _render(): void {
    this.element.hidden = this._count === 0;
    this._label.textContent = this._count === 1 ? "1 table" : `${this._count} tables`;
    this.element.classList.toggle("table-indicator--active", this._active);
    this.element.setAttribute("aria-pressed", this._active ? "true" : "false");
    this.element.title = this._active
      ? "Hide detected tables"
      : `Show the ${this._count === 1 ? "table" : `${this._count} tables`} detected in this document`;
  }
}
