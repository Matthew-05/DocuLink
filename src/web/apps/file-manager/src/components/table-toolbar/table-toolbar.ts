import type { FolderEntry } from "../../types/index.js";

export interface TableToolbarOptions {
  onRemoveSelected(): void;
  onMoveSelected(folderId: string | null): void;
  onOcrSelected(): void;
  onFilterChange(text: string): void;
}

export class TableToolbar {
  private readonly _root: HTMLElement;
  private readonly _filterInput: HTMLInputElement;
  private readonly _moveBtn: HTMLButtonElement;
  private readonly _moveDropdown: HTMLElement;
  private readonly _ocrBtn: HTMLButtonElement;
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

    // OCR button
    this._ocrBtn = document.createElement("button");
    this._ocrBtn.className = "btn-toolbar-move";
    this._ocrBtn.disabled = true;
    this._ocrBtn.textContent = "OCR Selected (0)";
    this._ocrBtn.title = "Add a searchable text layer to the selected PDFs using OCR";
    this._ocrBtn.addEventListener("click", () => options.onOcrSelected());

    // Remove button (rightmost)
    this._removeBtn = document.createElement("button");
    this._removeBtn.className = "btn-toolbar-danger";
    this._removeBtn.disabled = true;
    this._removeBtn.textContent = "Remove Selected (0)";
    this._removeBtn.addEventListener("click", () => options.onRemoveSelected());

    rightGroup.appendChild(moveWrap);
    rightGroup.appendChild(this._ocrBtn);
    rightGroup.appendChild(this._removeBtn);

    this._root.appendChild(this._filterInput);
    this._root.appendChild(rightGroup);
    container.appendChild(this._root);

    // Close dropdown on any outside click
    document.addEventListener("click", () => this._closeDropdown());
  }

  update(selectedCount: number): void {
    this._moveBtn.disabled = selectedCount === 0;
    this._ocrBtn.disabled = selectedCount === 0;
    this._ocrBtn.textContent = `OCR Selected (${selectedCount})`;
    this._removeBtn.disabled = selectedCount === 0;
    this._removeBtn.textContent = `Remove Selected (${selectedCount})`;
    if (selectedCount === 0) this._closeDropdown();
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
