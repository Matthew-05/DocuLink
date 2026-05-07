import type { FileEntry } from "../../types/index.js";
import { sendRenameFile, sendRemoveFile } from "../../host-bridge.js";

export class FileTable {
  private readonly _root: HTMLElement;
  private readonly _tbody: HTMLTableSectionElement;
  private _files: FileEntry[] = [];
  private _selectedFolderId: string | null = null;

  constructor(container: HTMLElement) {
    this._root = document.createElement("div");
    this._root.className = "file-table-wrap";

    const table = document.createElement("table");
    table.className = "file-table";

    const thead = document.createElement("thead");
    thead.innerHTML = `
      <tr>
        <th class="col-name">File Name</th>
        <th class="col-status">Status</th>
        <th class="col-size">Size</th>
        <th class="col-date">Date Added</th>
        <th class="col-actions"></th>
      </tr>`;

    this._tbody = document.createElement("tbody");

    table.appendChild(thead);
    table.appendChild(this._tbody);
    this._root.appendChild(table);
    container.appendChild(this._root);
  }

  update(files: FileEntry[], selectedFolderId: string | null): void {
    this._files = files;
    this._selectedFolderId = selectedFolderId;
    this._render();
  }

  private _visibleFiles(): FileEntry[] {
    if (this._selectedFolderId === null) return this._files;
    return this._files.filter((f) => (f.folderId ?? null) === this._selectedFolderId);
  }

  private _render(): void {
    this._tbody.innerHTML = "";
    const visible = this._visibleFiles();

    if (visible.length === 0) {
      const empty = document.createElement("tr");
      empty.innerHTML = `<td colspan="5" class="file-table__empty">No files here yet. Drop PDFs to add them.</td>`;
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

    // Name cell — double-click to rename
    const nameTd = document.createElement("td");
    nameTd.className = "col-name";
    const nameSpan = document.createElement("span");
    nameSpan.className = "file-name";
    nameSpan.textContent = file.name;
    nameSpan.title = "Double-click to rename";
    nameSpan.addEventListener("dblclick", () => this._startRename(file, nameSpan, tr));
    nameTd.appendChild(nameSpan);

    // Status cell
    const statusTd = document.createElement("td");
    statusTd.className = "col-status";
    const badge = document.createElement("span");
    badge.className = `status-badge status-badge--${file.status}`;
    badge.textContent = file.status;
    statusTd.appendChild(badge);

    // Size cell
    const sizeTd = document.createElement("td");
    sizeTd.className = "col-size";
    sizeTd.textContent = formatBytes(file.fileSizeBytes);

    // Date cell
    const dateTd = document.createElement("td");
    dateTd.className = "col-date";
    dateTd.textContent = formatDate(file.dateAdded);

    // Actions cell
    const actionsTd = document.createElement("td");
    actionsTd.className = "col-actions";

    const renameBtn = document.createElement("button");
    renameBtn.className = "icon-btn icon-btn--sm";
    renameBtn.title = "Rename";
    renameBtn.textContent = "✎";
    renameBtn.addEventListener("click", () => this._startRename(file, nameSpan, tr));

    const removeBtn = document.createElement("button");
    removeBtn.className = "icon-btn icon-btn--sm icon-btn--danger";
    removeBtn.title = "Remove file";
    removeBtn.textContent = "✕";
    removeBtn.addEventListener("click", () => this._confirmRemove(file, actionsTd));

    actionsTd.appendChild(renameBtn);
    actionsTd.appendChild(removeBtn);

    tr.appendChild(nameTd);
    tr.appendChild(statusTd);
    tr.appendChild(sizeTd);
    tr.appendChild(dateTd);
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

  private _confirmRemove(file: FileEntry, actionsTd: HTMLTableCellElement): void {
    if (actionsTd.querySelector(".inline-confirm")) return;

    const original = actionsTd.innerHTML;
    actionsTd.innerHTML = "";

    const wrap = document.createElement("span");
    wrap.className = "inline-confirm";

    const label = document.createElement("span");
    label.className = "inline-confirm__label";
    label.textContent = "Remove?";

    const yesBtn = document.createElement("button");
    yesBtn.className = "icon-btn icon-btn--sm icon-btn--danger";
    yesBtn.textContent = "Yes";

    const noBtn = document.createElement("button");
    noBtn.className = "icon-btn icon-btn--sm";
    noBtn.textContent = "No";

    wrap.appendChild(label);
    wrap.appendChild(yesBtn);
    wrap.appendChild(noBtn);
    actionsTd.appendChild(wrap);

    yesBtn.addEventListener("click", () => sendRemoveFile(file.id));
    noBtn.addEventListener("click", () => { actionsTd.innerHTML = original; });
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
