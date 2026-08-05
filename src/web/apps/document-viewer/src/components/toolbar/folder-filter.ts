import type { FolderEntry } from "../../types/index.js";

const ALL_FOLDERS_LABEL = "All folders";

type FolderChangedCallback = (folderId: string | null) => void;

/**
 * Dropdown that narrows the document selector (and cross-document search) to a
 * single folder. `null` means "All folders" and is the default.
 */
export class FolderFilter {
  readonly element: HTMLElement;

  private _folders: FolderEntry[] = [];
  private _selectedId: string | null = null;
  private _isOpen = false;

  private readonly _triggerLabel: HTMLSpanElement;
  private readonly _list: HTMLUListElement;
  private readonly _callbacks: FolderChangedCallback[] = [];
  private readonly _onOpenCallbacks: Array<() => void> = [];

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "folder-filter toolbar__slot";

    const trigger = document.createElement("button");
    trigger.className = "folder-filter__trigger";
    trigger.title = "Filter documents by folder";
    trigger.addEventListener("click", (e) => {
      e.stopPropagation();
      this._toggle();
    });

    const icon = document.createElement("span");
    icon.className = "folder-filter__icon";
    icon.textContent = "🗀";

    this._triggerLabel = document.createElement("span");
    this._triggerLabel.className = "folder-filter__trigger-label";
    this._triggerLabel.textContent = ALL_FOLDERS_LABEL;

    const caret = document.createElement("span");
    caret.className = "folder-filter__caret";
    caret.textContent = "▾";

    trigger.append(icon, this._triggerLabel, caret);

    const dropdown = document.createElement("div");
    dropdown.className = "folder-filter__dropdown";

    this._list = document.createElement("ul");
    this._list.className = "folder-filter__list";

    dropdown.appendChild(this._list);
    this.element.append(trigger, dropdown);

    document.addEventListener("click", () => this._close());

    this._renderList();
  }

  onChange(cb: FolderChangedCallback): void {
    this._callbacks.push(cb);
  }

  onOpen(cb: () => void): void {
    this._onOpenCallbacks.push(cb);
  }

  close(): void {
    this._close();
  }

  /** Folder currently filtered on, or `null` for all folders. */
  getSelectedFolderId(): string | null {
    return this._selectedId;
  }

  /**
   * Replaces the folder list. A selection whose folder no longer exists falls
   * back to "All folders" and notifies listeners.
   */
  setFolders(folders: FolderEntry[]): void {
    this._folders = folders;

    if (this._selectedId !== null
      && !folders.some((folder) => folder.id === this._selectedId)) {
      this._select(null);
      return;
    }

    this._syncTriggerLabel();
    this._renderList();
  }

  private _labelFor(id: string | null): string {
    if (id === null) return ALL_FOLDERS_LABEL;
    return this._folders.find((folder) => folder.id === id)?.name ?? ALL_FOLDERS_LABEL;
  }

  private _syncTriggerLabel(): void {
    this._triggerLabel.textContent = this._labelFor(this._selectedId);
    this.element.classList.toggle("folder-filter--active", this._selectedId !== null);
  }

  private _toggle(): void {
    this._isOpen ? this._close() : this._open();
  }

  private _open(): void {
    this._isOpen = true;
    this.element.classList.add("folder-filter--open");
    this._renderList();
    for (const cb of this._onOpenCallbacks) cb();
  }

  private _close(): void {
    this._isOpen = false;
    this.element.classList.remove("folder-filter--open");
  }

  private _renderList(): void {
    this._list.replaceChildren();

    this._list.appendChild(this._createItem(null));
    for (const folder of this._folders) {
      this._list.appendChild(this._createItem(folder.id));
    }
  }

  private _createItem(id: string | null): HTMLLIElement {
    const li = document.createElement("li");
    li.className = "folder-filter__item";
    if (id === this._selectedId) {
      li.classList.add("folder-filter__item--active");
    }

    const label = this._labelFor(id);
    li.textContent = label;
    li.title = label;

    li.addEventListener("click", (e) => {
      e.stopPropagation();
      this._close();
      if (id === this._selectedId) return;
      this._select(id);
    });

    return li;
  }

  private _select(id: string | null): void {
    this._selectedId = id;
    this._syncTriggerLabel();
    this._renderList();
    for (const cb of this._callbacks) cb(id);
  }
}
