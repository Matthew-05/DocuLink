/**
 * The notice that detection found tables in this document.
 *
 * Detection is a suggestion, not a result: a wrong table is worse than no
 * table, so this says its piece once and then gets out of the way. Dismissing
 * it leaves nothing behind on the page — the toolbar button is what brings the
 * suggestions back.
 */
export class TableNotice {
  readonly element: HTMLDivElement;

  private readonly _text: HTMLSpanElement;
  private readonly _showCallbacks: Array<() => void> = [];
  private readonly _dismissCallbacks: Array<() => void> = [];
  private _count = 0;
  private _visible = false;

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "table-notice";
    this.element.hidden = true;
    this.element.setAttribute("role", "status");

    this._text = document.createElement("span");
    this._text.className = "table-notice__text";

    const show = document.createElement("button");
    show.type = "button";
    show.className = "table-notice__action";
    show.textContent = "Show";
    show.addEventListener("click", () => {
      for (const callback of this._showCallbacks) callback();
    });

    const dismiss = document.createElement("button");
    dismiss.type = "button";
    dismiss.className = "table-notice__dismiss";
    dismiss.textContent = "×";
    dismiss.title = "Dismiss";
    dismiss.setAttribute("aria-label", "Dismiss");
    dismiss.addEventListener("click", () => {
      for (const callback of this._dismissCallbacks) callback();
    });

    this.element.append(this._text, show, dismiss);
    this._render();
  }

  /** "Show" was pressed: reveal the suggestions and put the notice away. */
  onShow(callback: () => void): void {
    this._showCallbacks.push(callback);
  }

  onDismiss(callback: () => void): void {
    this._dismissCallbacks.push(callback);
  }

  setCount(count: number): void {
    this._count = Math.max(0, count);
    this._render();
  }

  setVisible(visible: boolean): void {
    this._visible = visible;
    this._render();
  }

  private _render(): void {
    this.element.hidden = !this._visible || this._count === 0;
    this._text.textContent = this._count === 1
      ? "1 table detected"
      : `${this._count} tables detected`;
  }
}
