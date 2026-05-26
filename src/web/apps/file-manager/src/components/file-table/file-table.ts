import type { FileEntry, FolderEntry } from "../../types/index.js";
import { sendRenameFile, sendRemoveFile } from "../../host-bridge.js";

export interface FileTableOptions {
  onSelectionChange(selectedIds: string[]): void;
}

export class FileTable {
  private readonly _root: HTMLElement;
  private readonly _thead: HTMLTableSectionElement;
  private readonly _tbody: HTMLTableSectionElement;
  private readonly _contextMenu: HTMLDivElement;
  private _files: FileEntry[] = [];
  private _folders: FolderEntry[] = [];
  private _selectedFolderId: string | null = null;
  private _selectedIds: Set<string> = new Set();
  private _filterText = "";
  private _contextMenuFile: FileEntry | null = null;
  private _contextMenuNameSpan: HTMLSpanElement | null = null;
  private _contextMenuRow: HTMLTableRowElement | null = null;
  private _isLoading = true;
  private readonly _onSelectionChange: (ids: string[]) => void;

  constructor(container: HTMLElement, options: FileTableOptions) {
    this._onSelectionChange = options.onSelectionChange;

    this._root = document.createElement("div");
    this._root.className = "file-table-wrap";

    const table = document.createElement("table");
    table.className = "file-table";

    this._thead = document.createElement("thead");
    this._thead.innerHTML = `
      <tr>
        <th class="col-check"><input type="checkbox" class="select-all-cb" title="Select all" /></th>
        <th class="col-name">File Name</th>
        <th class="col-status">Status</th>
        <th class="col-size">Size</th>
        <th class="col-date">Date Added</th>
        <th class="col-folder">Folder</th>
        <th class="col-actions"></th>
      </tr>`;

    const selectAllCb = this._thead.querySelector<HTMLInputElement>(".select-all-cb")!;
    selectAllCb.addEventListener("change", () => this._onSelectAll(selectAllCb.checked));

    this._tbody = document.createElement("tbody");

    table.appendChild(this._thead);
    table.appendChild(this._tbody);
    this._root.appendChild(table);

    // Create context menu
    this._contextMenu = document.createElement("div");
    this._contextMenu.className = "file-table__context-menu";
    this._contextMenu.innerHTML = `
      <button class="file-table__context-item" data-action="rename">Rename</button>
      <button class="file-table__context-item" data-action="delete">Delete</button>
    `;
    this._root.appendChild(this._contextMenu);

    // Hide context menu on outside click
    document.addEventListener("click", () => this._hideContextMenu());

    container.appendChild(this._root);
  }

  update(files: FileEntry[], selectedFolderId: string | null): void {
    this._isLoading = false;
    this._files = files;
    this._selectedFolderId = selectedFolderId;
    // Drop selections that no longer exist
    const fileIds = new Set(files.map((f) => f.id));
    for (const id of this._selectedIds) {
      if (!fileIds.has(id)) this._selectedIds.delete(id);
    }
    this._render();
  }

  setFilter(text: string): void {
    this._filterText = text;
    this._selectedIds.clear();
    this._render();
    this._onSelectionChange([]);
  }

  getSelectedIds(): string[] {
    return Array.from(this._selectedIds);
  }

  clearSelection(): void {
    this._selectedIds.clear();
    this._render();
    this._onSelectionChange([]);
  }

  /** Clears row selection and the filter text. */
  reset(): void {
    this._filterText = "";
    this.clearSelection();
  }

  updateFolders(folders: FolderEntry[]): void {
    this._folders = folders;
    this._render();
  }

  private _visibleFiles(): FileEntry[] {
    let files = this._selectedFolderId === null
      ? this._files
      : this._files.filter((f) => (f.folderId ?? null) === this._selectedFolderId);

    if (this._filterText.trim() !== "") {
      const lower = this._filterText.toLowerCase();
      files = files.filter((f) => f.name.toLowerCase().includes(lower));
    }

    return files;
  }

  private _onSelectAll(checked: boolean): void {
    const visible = this._visibleFiles();
    if (checked) {
      for (const f of visible) this._selectedIds.add(f.id);
    } else {
      for (const f of visible) this._selectedIds.delete(f.id);
    }
    this._render();
    this._onSelectionChange(this.getSelectedIds());
  }

  private _onRowCheck(id: string, checked: boolean): void {
    if (checked) {
      this._selectedIds.add(id);
    } else {
      this._selectedIds.delete(id);
    }
    this._updateSelectAllCheckbox();
    this._onSelectionChange(this.getSelectedIds());
  }

  private _updateSelectAllCheckbox(): void {
    const selectAllCb = this._thead.querySelector<HTMLInputElement>(".select-all-cb");
    if (!selectAllCb) return;
    const visible = this._visibleFiles();
    const checkedCount = visible.filter((f) => this._selectedIds.has(f.id)).length;
    selectAllCb.checked = visible.length > 0 && checkedCount === visible.length;
    selectAllCb.indeterminate = checkedCount > 0 && checkedCount < visible.length;
  }

  private _render(): void {
    this._tbody.innerHTML = "";
    const visible = this._visibleFiles();

    this._updateSelectAllCheckbox();

    if (visible.length === 0) {
      const empty = document.createElement("tr");
      empty.innerHTML = this._isLoading
        ? `<td colspan="7" class="file-table__empty">DocuLink Initializing…</td>`
        : `<td colspan="7" class="file-table__empty">No files here yet. Drop PDFs to add them.</td>`;
      this._tbody.appendChild(empty);
      return;
    }

    for (const file of visible) {
      this._tbody.appendChild(this._buildRow(file));
    }
  }

  private _buildRow(file: FileEntry): HTMLTableRowElement {
    const tr = document.createElement("tr");
    tr.dataset["id"] = file.id;
    if (this._selectedIds.has(file.id)) tr.classList.add("is-selected");

    // Row-level click → toggle selection (ignore clicks on interactive children)
    tr.addEventListener("click", (e) => {
      const target = e.target as HTMLElement;
      if (
        target.tagName === "BUTTON" ||
        target.tagName === "INPUT" ||
        target.closest("button") ||
        target.closest("input")
      ) return;
      const nowChecked = !this._selectedIds.has(file.id);
      cb.checked = nowChecked;
      this._onRowCheck(file.id, nowChecked);
      tr.classList.toggle("is-selected", nowChecked);
    });

    // Row-level right-click → context menu
    tr.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      e.stopPropagation();
      this._showContextMenu(file, nameSpan, tr, e.clientX, e.clientY);
    });

    // Checkbox cell
    const checkTd = document.createElement("td");
    checkTd.className = "col-check";
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.className = "row-cb";
    cb.checked = this._selectedIds.has(file.id);
    cb.addEventListener("change", () => {
      this._onRowCheck(file.id, cb.checked);
      tr.classList.toggle("is-selected", cb.checked);
    });
    cb.addEventListener("click", (e) => e.stopPropagation());
    checkTd.appendChild(cb);

    // Name cell — double-click to rename
    const nameTd = document.createElement("td");
    nameTd.className = "col-name";
    const nameSpan = document.createElement("span");
    nameSpan.className = "file-name";
    nameSpan.textContent = file.name;
    nameSpan.title = "Double-click to rename";
    nameSpan.addEventListener("dblclick", (e) => {
      e.stopPropagation();
      this._startRename(file, nameSpan, tr);
    });
    nameTd.appendChild(nameSpan);

    // Status cell
    const statusTd = document.createElement("td");
    statusTd.className = "col-status";
    const badge = document.createElement("span");
    badge.className = `status-badge status-badge--${file.status}`;
    badge.textContent = formatStatusLabel(file.status);
    statusTd.appendChild(badge);

    // Size cell
    const sizeTd = document.createElement("td");
    sizeTd.className = "col-size";
    sizeTd.textContent = formatBytes(file.fileSizeBytes);

    // Date cell
    const dateTd = document.createElement("td");
    dateTd.className = "col-date";
    dateTd.textContent = formatDate(file.dateAdded);

    // Folder cell
    const folderTd = document.createElement("td");
    folderTd.className = "col-folder";
    if (file.folderId) {
      const folder = this._folders.find((f) => f.id === file.folderId);
      if (folder) folderTd.textContent = folder.name;
    }

    // Actions cell — rename only
    const actionsTd = document.createElement("td");
    actionsTd.className = "col-actions";

    const renameBtn = document.createElement("button");
    renameBtn.className = "icon-btn icon-btn--sm";
    renameBtn.title = "Rename";
    renameBtn.textContent = "✎";
    renameBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      this._startRename(file, nameSpan, tr);
    });

    actionsTd.appendChild(renameBtn);

    tr.appendChild(checkTd);
    tr.appendChild(nameTd);
    tr.appendChild(statusTd);
    tr.appendChild(sizeTd);
    tr.appendChild(dateTd);
    tr.appendChild(folderTd);
    tr.appendChild(actionsTd);

    return tr;
  }

  private _startRename(file: FileEntry, nameSpan: HTMLSpanElement, tr: HTMLTableRowElement): void {
    if (tr.querySelector(".rename-input")) return;

    const input = document.createElement("input");
    input.className = "rename-input";
    input.type = "text";
    input.value = file.name;
    nameSpan.replaceWith(input);
    input.focus();
    input.select();

    const commit = (): void => {
      const newName = input.value.trim();
      if (newName && newName !== file.name) {
        sendRenameFile(file.id, newName);
      } else {
        input.replaceWith(nameSpan);
      }
    };

    input.addEventListener("blur", commit);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { input.blur(); }
      if (e.key === "Escape") { input.replaceWith(nameSpan); }
    });
  }

  private _showContextMenu(file: FileEntry, nameSpan: HTMLSpanElement, tr: HTMLTableRowElement, x: number, y: number): void {
    this._contextMenuFile = file;
    this._contextMenuNameSpan = nameSpan;
    this._contextMenuRow = tr;

    this._contextMenu.style.position = "fixed";
    this._contextMenu.style.left = `${x}px`;
    this._contextMenu.style.top = `${y}px`;
    this._contextMenu.classList.add("file-table__context-menu--visible");

    // Attach event listeners to menu items
    const items = this._contextMenu.querySelectorAll<HTMLButtonElement>(".file-table__context-item");
    items.forEach(item => {
      item.removeEventListener("click", this._handleContextMenuClick);
      item.addEventListener("click", (e) => this._handleContextMenuClick(e));
    });
  }

  private _hideContextMenu(): void {
    this._contextMenu.classList.remove("file-table__context-menu--visible");
    this._contextMenuFile = null;
    this._contextMenuNameSpan = null;
    this._contextMenuRow = null;
  }

  private _handleContextMenuClick = (e: MouseEvent): void => {
    const button = e.target as HTMLButtonElement;
    const action = button.dataset["action"];

    if (!this._contextMenuFile || !this._contextMenuNameSpan || !this._contextMenuRow) return;

    if (action === "rename") {
      this._startRename(this._contextMenuFile, this._contextMenuNameSpan, this._contextMenuRow);
    } else if (action === "delete") {
      sendRemoveFile(this._contextMenuFile.id);
    }

    this._hideContextMenu();
  };
}

function formatStatusLabel(status: string): string {
  switch (status) {
    case "ocr":  return "OCR";
    case "text": return "Text";
    case "none": return "None";
    default:     return status;
  }
}

function formatBytes(bytes: number): string {
  if (bytes <= 0) return "—";
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatDate(iso: string): string {
  if (!iso) return "—";
  try {
    return new Date(iso).toLocaleDateString(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric",
    });
  } catch {
    return iso;
  }
}
