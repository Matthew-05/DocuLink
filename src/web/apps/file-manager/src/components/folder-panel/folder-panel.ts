import type { FolderEntry, FileEntry } from "../../types/index.js";
import {
  sendAddFolder,
  sendRenameFolder,
  sendRemoveFolder,
} from "../../host-bridge.js";

export interface FolderPanelCallbacks {
  onSelectionChange(folderId: string | null): void;
}

export class FolderPanel {
  private readonly _root: HTMLElement;
  private readonly _list: HTMLUListElement;
  private _folders: FolderEntry[] = [];
  private _files: FileEntry[] = [];
  private _selectedId: string | null = null;
  private readonly _callbacks: FolderPanelCallbacks;

  constructor(container: HTMLElement, callbacks: FolderPanelCallbacks) {
    this._callbacks = callbacks;

    this._root = document.createElement("div");
    this._root.className = "folder-panel";

    const header = document.createElement("div");
    header.className = "folder-panel__header";

    const title = document.createElement("span");
    title.className = "folder-panel__title";
    title.textContent = "Folders";

    const addBtn = document.createElement("button");
    addBtn.className = "icon-btn";
    addBtn.title = "New folder";
    addBtn.textContent = "+";
    addBtn.tabIndex = -1;
    addBtn.addEventListener("click", () => this._showNewFolderRow());

    header.appendChild(title);
    header.appendChild(addBtn);

    this._list = document.createElement("ul");
    this._list.className = "folder-panel__list";

    this._root.appendChild(header);
    this._root.appendChild(this._list);
    container.appendChild(this._root);

    this._renderItems();
  }

  update(folders: FolderEntry[], files: FileEntry[] = this._files): void {
    this._folders = folders;
    this._files = files;
    if (this._selectedId !== null && !folders.find((f) => f.id === this._selectedId)) {
      this._selectedId = null;
      this._callbacks.onSelectionChange(null);
    }
    this._renderItems();
  }

  getSelectedId(): string | null {
    return this._selectedId;
  }

  /** Resets folder selection and cancels any inline edit UI. */
  reset(): void {
    this._selectedId = null;
    this._renderItems();
  }

  private _renderItems(): void {
    this._list.innerHTML = "";

    this._list.appendChild(this._createItem(null, "All Files", this._files.length));

    for (const folder of this._folders) {
      const count = this._files.filter((f) => f.folderId === folder.id).length;
      this._list.appendChild(this._createItem(folder.id, folder.name, count));
    }
  }

  private _createItem(id: string | null, label: string, count: number): HTMLLIElement {
    const li = document.createElement("li");
    li.className =
      "folder-panel__item" +
      (this._selectedId === id ? " folder-panel__item--active" : "");
    li.dataset["folderId"] = id ?? "";

    const nameSpan = document.createElement("span");
    nameSpan.className = "folder-panel__item-name";
    nameSpan.textContent = label;
    li.appendChild(nameSpan);

    const countBadge = document.createElement("span");
    countBadge.className = "folder-panel__item-count";
    countBadge.textContent = String(count);
    li.appendChild(countBadge);

    li.addEventListener("click", () => {
      this._selectedId = id;
      this._renderItems();
      this._callbacks.onSelectionChange(id);
    });

    if (id !== null) {
      const actions = document.createElement("span");
      actions.className = "folder-panel__item-actions";

      const renameBtn = document.createElement("button");
      renameBtn.className = "icon-btn icon-btn--sm";
      renameBtn.title = "Rename folder";
      renameBtn.textContent = "✎";
      renameBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        this._startInlineRename(li, nameSpan, id, label);
      });

      const deleteBtn = document.createElement("button");
      deleteBtn.className = "icon-btn icon-btn--sm icon-btn--danger";
      deleteBtn.title = "Delete folder";
      deleteBtn.textContent = "✕";
      deleteBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        this._showInlineDeleteConfirm(li, id);
      });

      actions.appendChild(renameBtn);
      actions.appendChild(deleteBtn);
      li.appendChild(actions);
    }

    return li;
  }

  /** Appends a temporary new-folder input row at the bottom of the list. */
  private _showNewFolderRow(): void {
    if (this._list.querySelector(".folder-panel__new-row")) return;

    const li = document.createElement("li");
    li.className = "folder-panel__item folder-panel__new-row";

    const input = document.createElement("input");
    input.className = "folder-inline-input";
    input.type = "text";
    input.placeholder = "Folder name…";
    input.maxLength = 64;

    const confirmBtn = document.createElement("button");
    confirmBtn.className = "icon-btn icon-btn--sm icon-btn--confirm";
    confirmBtn.textContent = "✓";
    confirmBtn.title = "Create folder";

    const cancelBtn = document.createElement("button");
    cancelBtn.className = "icon-btn icon-btn--sm";
    cancelBtn.textContent = "✕";
    cancelBtn.title = "Cancel";

    li.appendChild(input);
    li.appendChild(confirmBtn);
    li.appendChild(cancelBtn);
    this._list.appendChild(li);

    input.focus();

    const commit = (): void => {
      const name = input.value.trim();
      if (name) sendAddFolder(name);
      li.remove();
    };

    const cancel = (): void => li.remove();

    confirmBtn.addEventListener("click", (e) => { e.stopPropagation(); commit(); });
    cancelBtn.addEventListener("click", (e) => { e.stopPropagation(); cancel(); });
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); commit(); }
      if (e.key === "Escape") { e.preventDefault(); cancel(); }
    });
  }

  /** Replaces the folder name span with an inline input for renaming. */
  private _startInlineRename(
    li: HTMLLIElement,
    nameSpan: HTMLSpanElement,
    id: string,
    currentName: string
  ): void {
    if (li.querySelector(".folder-inline-input")) return;

    const input = document.createElement("input");
    input.className = "folder-inline-input";
    input.type = "text";
    input.value = currentName;
    input.maxLength = 64;

    nameSpan.replaceWith(input);
    input.focus();
    input.select();

    const commit = (): void => {
      const name = input.value.trim();
      if (name && name !== currentName) {
        sendRenameFolder(id, name);
      } else {
        input.replaceWith(nameSpan);
      }
    };

    input.addEventListener("blur", commit);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); input.blur(); }
      if (e.key === "Escape") { e.preventDefault(); input.replaceWith(nameSpan); }
    });
  }

  /** Replaces the item's action buttons with an inline delete confirmation. */
  private _showInlineDeleteConfirm(li: HTMLLIElement, id: string): void {
    if (li.querySelector(".folder-panel__delete-confirm")) return;

    const actionsSpan = li.querySelector<HTMLElement>(".folder-panel__item-actions");
    if (!actionsSpan) return;

    const confirmEl = document.createElement("span");
    confirmEl.className = "folder-panel__delete-confirm";

    const label = document.createElement("span");
    label.className = "delete-confirm-label";
    label.textContent = "Delete?";

    const yesBtn = document.createElement("button");
    yesBtn.className = "icon-btn icon-btn--sm icon-btn--danger";
    yesBtn.textContent = "Yes";

    const noBtn = document.createElement("button");
    noBtn.className = "icon-btn icon-btn--sm";
    noBtn.textContent = "No";

    confirmEl.appendChild(label);
    confirmEl.appendChild(yesBtn);
    confirmEl.appendChild(noBtn);

    actionsSpan.replaceWith(confirmEl);

    yesBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      sendRemoveFolder(id);
    });

    noBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      confirmEl.replaceWith(actionsSpan);
    });
  }
}
