const MENU_CLASS = "rect-context-menu";
const ITEM_CLASS = "rect-context-menu__item";
const VIEWPORT_MARGIN_PX = 8;

export interface ContextMenuPosition {
  left: number;
  top: number;
}

/** Positions a menu beside its anchor while keeping it inside the viewport. */
export function getViewportAwareMenuPosition(
  anchorX: number,
  anchorY: number,
  menuWidth: number,
  menuHeight: number,
  viewportWidth: number,
  viewportHeight: number,
  margin = VIEWPORT_MARGIN_PX,
): ContextMenuPosition {
  const maxLeft = Math.max(margin, viewportWidth - menuWidth - margin);
  const maxTop = Math.max(margin, viewportHeight - menuHeight - margin);

  const preferredLeft = anchorX + menuWidth + margin > viewportWidth
    ? anchorX - menuWidth
    : anchorX;
  const preferredTop = anchorY + menuHeight + margin > viewportHeight
    ? anchorY - menuHeight
    : anchorY;

  return {
    left: Math.min(maxLeft, Math.max(margin, preferredLeft)),
    top: Math.min(maxTop, Math.max(margin, preferredTop)),
  };
}

/**
 * A minimal floating context menu for link rectangle overlays.
 * Mounts a single menu element on document.body and repositions it per show().
 */
export class RectContextMenu {
  private readonly _element: HTMLDivElement;
  private readonly _onDeleteCallbacks: Array<(id: string) => void> = [];
  private readonly _onDeleteKeepDataCallbacks: Array<(id: string) => void> = [];
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

    const keepDataItem = this._createItem(
      "Delete link, keep cell data",
      this._onDeleteKeepDataCallbacks,
    );
    const divider = document.createElement("div");
    divider.className = "rect-context-menu__divider";
    const deleteItem = this._createItem("Delete link", this._onDeleteCallbacks);
    deleteItem.classList.add("rect-context-menu__item--danger");

    this._element.append(keepDataItem, divider, deleteItem);
    document.body.append(this._element);
  }

  /** Registers a callback invoked when the user chooses Delete Link. */
  onDelete(cb: (id: string) => void): void {
    this._onDeleteCallbacks.push(cb);
  }

  /** Registers a callback invoked when the link should be removed without clearing Excel data. */
  onDeleteKeepData(cb: (id: string) => void): void {
    this._onDeleteKeepDataCallbacks.push(cb);
  }

  /** Shows the menu at viewport coordinates for the given rectangle id. */
  show(clientX: number, clientY: number, rectId: string): void {
    this._activeRectId = rectId;
    this._element.hidden = false;
    const bounds = this._element.getBoundingClientRect();
    const position = getViewportAwareMenuPosition(
      clientX,
      clientY,
      bounds.width,
      bounds.height,
      document.documentElement.clientWidth,
      document.documentElement.clientHeight,
    );
    this._element.style.left = `${position.left}px`;
    this._element.style.top = `${position.top}px`;

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

  private _createItem(
    label: string,
    callbacks: Array<(id: string) => void>,
  ): HTMLButtonElement {
    const item = document.createElement("button");
    item.type = "button";
    item.className = ITEM_CLASS;
    item.textContent = label;
    item.addEventListener("click", () => {
      if (this._activeRectId === null) return;
      const id = this._activeRectId;
      this.hide();
      for (const callback of callbacks) callback(id);
    });
    return item;
  }
}
