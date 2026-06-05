import type { FolderEntry } from "../../types/index.js";

export interface TableToolbarOptions {
  onRemoveSelected(): void;
  onMoveSelected(folderId: string | null): void;
  onProcessSelected(): void;
  onCancelOcr(): void;
  onFilterChange(text: string): void;
}

export class TableToolbar {
  private readonly _root: HTMLElement;
  private readonly _filterInput: HTMLInputElement;
  private readonly _cancelBtn: HTMLButtonElement;
  private readonly _moveBtn: HTMLButtonElement;
  private readonly _moveDropdown: HTMLElement;
  private readonly _processBtn: HTMLButtonElement;
  private readonly _removeBtn: HTMLButtonElement;
  private readonly _onMoveSelected: (folderId: string | null) => void;
  private _folders: FolderEntry[] = [];
  private _dropdownOpen = false;

  constructor(container: HTMLElement, options: TableToolbarOptions) {
    this._onMoveSelected = options.onMoveSelected;

    this._root = document.createElement("div");
    this._root.className = "table-toolbar";

    // ── Filter input (left) ──────────────────────────────────────────────────
    this._filterInput = document.createElement("input");
    this._filterInput.type = "text";
    this._filterInput.className = "toolbar-filter";
    this._filterInput.placeholder = "Filter files…";
    this._filterInput.addEventListener("input", () => {
      options.onFilterChange(this._filterInput.value);
    });

    // ── Right-side button group ──────────────────────────────────────────────
    const rightGroup = document.createElement("div");
    rightGroup.className = "toolbar-right";

    // Cancel OCR button (hidden unless OCR is running)
    this._cancelBtn = document.createElement("button");
    this._cancelBtn.className = "btn-toolbar-cancel";
    this._cancelBtn.textContent = "Cancel OCR";
    this._cancelBtn.style.display = "none";
    this._cancelBtn.addEventListener("click", () => options.onCancelOcr());

    // Move button + dropdown wrapper (position: relative anchor)
    const moveWrap = document.createElement("div");
    moveWrap.className = "move-wrap";

    this._moveBtn = document.createElement("button");
    this._moveBtn.className = "btn-toolbar-move";
    this._moveBtn.disabled = true;
    this._moveBtn.textContent = "Move to ▾";
    this._moveBtn.addEventListener("click", (e) => {
      e.stopPropagation();
      this._toggleDropdown();
    });

    this._moveDropdown = document.createElement("div");
    this._moveDropdown.className = "move-dropdown";
    this._renderDropdownItems();

    moveWrap.appendChild(this._moveBtn);
    moveWrap.appendChild(this._moveDropdown);

    this._processBtn = document.createElement("button");
    this._processBtn.className = "btn-toolbar-move";
    this._processBtn.disabled = true;
    this._processBtn.textContent = "OCR";
    this._processBtn.title =
      "OCR scanned PDFs or replace an embedded text layer";
    this._processBtn.addEventListener("click", () => options.onProcessSelected());

    // Remove button (rightmost)
    this._removeBtn = document.createElement("button");
    this._removeBtn.className = "btn-toolbar-danger";
    this._removeBtn.disabled = true;
    this._removeBtn.textContent = "Remove Selected (0)";
    this._removeBtn.addEventListener("click", () => options.onRemoveSelected());

    rightGroup.appendChild(this._cancelBtn);
    rightGroup.appendChild(moveWrap);
    rightGroup.appendChild(this._processBtn);
    rightGroup.appendChild(this._removeBtn);

    this._root.appendChild(this._filterInput);
    this._root.appendChild(rightGroup);
    container.appendChild(this._root);

    // Close dropdown on any outside click
    document.addEventListener("click", () => this._closeDropdown());
  }

  update(selectedCount: number, selectedHasActiveOcr = false, anyOcrRunning = false): void {
    this._moveBtn.disabled = selectedCount === 0 || selectedHasActiveOcr;
    this._processBtn.disabled = selectedCount === 0 || selectedHasActiveOcr;
    this._removeBtn.disabled = selectedCount === 0 || selectedHasActiveOcr;
    this._removeBtn.textContent = `Remove Selected (${selectedCount})`;
    if (selectedCount === 0) this._closeDropdown();
    this._cancelBtn.style.display = anyOcrRunning ? "" : "none";
  }

  /** Clears the filter field, closes the move menu, and disables bulk actions. */
  reset(): void {
    this._filterInput.value = "";
    this._closeDropdown();
    this.update(0);
  }

  updateFolders(folders: FolderEntry[]): void {
    this._folders = folders;
    this._renderDropdownItems();
  }

  private _renderDropdownItems(): void {
    this._moveDropdown.innerHTML = "";

    const unfiledItem = document.createElement("div");
    unfiledItem.className = "move-dropdown__item move-dropdown__item--unfiled";
    unfiledItem.textContent = "No Folder";
    unfiledItem.addEventListener("click", (e) => {
      e.stopPropagation();
      this._onMoveSelected(null);
      this._closeDropdown();
    });
    this._moveDropdown.appendChild(unfiledItem);

    for (const folder of this._folders) {
      const item = document.createElement("div");
      item.className = "move-dropdown__item";
      item.textContent = folder.name;
      item.dataset["folderId"] = folder.id;
      item.addEventListener("click", (e) => {
        e.stopPropagation();
        this._onMoveSelected(folder.id);
        this._closeDropdown();
      });
      this._moveDropdown.appendChild(item);
    }
  }

  private _toggleDropdown(): void {
    if (this._dropdownOpen) {
      this._closeDropdown();
    } else {
      this._openDropdown();
    }
  }

  private _openDropdown(): void {
    this._dropdownOpen = true;
    this._moveDropdown.classList.add("move-dropdown--open");
  }

  private _closeDropdown(): void {
    this._dropdownOpen = false;
    this._moveDropdown.classList.remove("move-dropdown--open");
  }
}
