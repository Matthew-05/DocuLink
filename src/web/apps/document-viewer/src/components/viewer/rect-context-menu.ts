const MENU_CLASS = "rect-context-menu";
const ITEM_CLASS = "rect-context-menu__item";

/**
 * A minimal floating context menu for link rectangle overlays.
 * Mounts a single menu element on document.body and repositions it per show().
 */
export class RectContextMenu {
  private readonly _element: HTMLDivElement;
  private readonly _onDeleteCallbacks: Array<(id: string) => void> = [];
  private _activeRectId: string | null = null;

  private readonly _onDocClick = (e: MouseEvent): void => {
    if (!this._element.contains(e.target as Node)) this.hide();
  };

  private readonly _onKeyDown = (e: KeyboardEvent): void => {
    if (e.key === "Escape") this.hide();
  };

  private readonly _onScroll = (): void => {
    this.hide();
  };

  constructor() {
    this._element = document.createElement("div");
    this._element.className = MENU_CLASS;
    this._element.hidden = true;

    const item = document.createElement("button");
    item.type = "button";
    item.className = ITEM_CLASS;
    item.textContent = "Delete Link";
    item.addEventListener("click", () => {
      if (this._activeRectId !== null) {
        const id = this._activeRectId;
        this.hide();
        for (const cb of this._onDeleteCallbacks) cb(id);
      }
    });

    this._element.append(item);
    document.body.append(this._element);
  }

  /** Registers a callback invoked when the user chooses Delete Link. */
  onDelete(cb: (id: string) => void): void {
    this._onDeleteCallbacks.push(cb);
  }

  /** Shows the menu at viewport coordinates for the given rectangle id. */
  show(clientX: number, clientY: number, rectId: string): void {
    this._activeRectId = rectId;
    this._element.hidden = false;
    this._element.style.left = `${clientX}px`;
    this._element.style.top = `${clientY}px`;

    document.addEventListener("click", this._onDocClick, true);
    document.addEventListener("keydown", this._onKeyDown);
    window.addEventListener("scroll", this._onScroll, true);
  }

  hide(): void {
    this._activeRectId = null;
    this._element.hidden = true;

    document.removeEventListener("click", this._onDocClick, true);
    document.removeEventListener("keydown", this._onKeyDown);
    window.removeEventListener("scroll", this._onScroll, true);
  }

  /** Attaches a scroll listener on a specific element (e.g. the viewer). */
  attachScrollTarget(element: HTMLElement): void {
    element.addEventListener("scroll", this._onScroll, { passive: true });
  }
}
