import type { FolderInfo, KeyColumnInfo, OutputColumnInfo } from "../../types/index.js";

export interface ConfigViewCallbacks {
  onStart: (outputColNumbers: number[], folderIds: string[]) => void;
  onCancel: () => void;
}

export class ConfigView {
  private readonly _el: HTMLElement;
  private readonly _keyColumns: KeyColumnInfo[];
  private readonly _outputColumns: OutputColumnInfo[];
  private readonly _folders: FolderInfo[];
  private _startBtn!: HTMLButtonElement;
  private _folderChecks = new Map<string, HTMLInputElement>();
  /** One selected output column number per key column, in the same order. */
  private _outputColNumbers: number[];

  constructor(
    container: HTMLElement,
    keyColumns: KeyColumnInfo[],
    outputColumns: OutputColumnInfo[],
    folders: FolderInfo[],
    rowCount: number,
    callbacks: ConfigViewCallbacks,
  ) {
    this._keyColumns = keyColumns;
    this._outputColumns = outputColumns;
    this._folders = folders;
    // Default each key column's output to the first available output column
    this._outputColNumbers = keyColumns.map((_, i) => outputColumns[i]?.colNumber ?? outputColumns[0]?.colNumber ?? 0);

    this._el = document.createElement("div");
    this._el.className = "config-view";
    container.appendChild(this._el);

    this._build(rowCount, callbacks);
    this._validate();
  }

  private _build(rowCount: number, callbacks: ConfigViewCallbacks): void {
    const dataRows = Math.max(0, rowCount - 1);

    const header = document.createElement("div");
    header.className = "config-view__header";
    header.innerHTML = `<span class="config-view__rowcount">${dataRows} data row${dataRows !== 1 ? "s" : ""}</span>`;
    this._el.appendChild(header);

    const body = document.createElement("div");
    body.className = "config-view__body";
    this._el.appendChild(body);

    // Column mapping section
    const mappingSection = document.createElement("div");
    mappingSection.className = "config-view__section";
    mappingSection.innerHTML = `<div class="config-view__section-title">Column Mapping</div>`;

    if (this._keyColumns.length === 0) {
      const empty = document.createElement("p");
      empty.className = "config-view__empty";
      empty.textContent = "No columns detected. Select one or more column ranges in Excel before opening the matcher.";
      mappingSection.appendChild(empty);
    } else if (this._outputColumns.length === 0) {
      const empty = document.createElement("p");
      empty.className = "config-view__empty";
      empty.textContent = "No output columns available. The selected key columns must not occupy the last column in the sheet.";
      mappingSection.appendChild(empty);
    } else {
      const pairsHeader = document.createElement("div");
      pairsHeader.className = "config-view__pairs-header";
      pairsHeader.innerHTML = `<span>Key Column</span><span></span><span>Output Column</span>`;
      mappingSection.appendChild(pairsHeader);

      const pairsContainer = document.createElement("div");
      pairsContainer.className = "config-view__pairs";

      this._keyColumns.forEach((keyCol, i) => {
        const row = document.createElement("div");
        row.className = "config-view__pair-row";

        const keyLabel = document.createElement("div");
        keyLabel.className = "config-view__key-label";
        keyLabel.title = keyCol.rangeAddress;
        keyLabel.textContent = keyCol.header;

        const arrow = document.createElement("span");
        arrow.className = "config-view__arrow";
        arrow.textContent = "→";

        const outSelect = this._makeOutputSelect(i);

        row.appendChild(keyLabel);
        row.appendChild(arrow);
        row.appendChild(outSelect);
        pairsContainer.appendChild(row);
      });

      mappingSection.appendChild(pairsContainer);
    }

    body.appendChild(mappingSection);

    // Folder section
    const folderSection = document.createElement("div");
    folderSection.className = "config-view__section";
    folderSection.innerHTML = `<div class="config-view__section-title">Search in Folders</div>`;

    if (this._folders.length === 0) {
      const empty = document.createElement("p");
      empty.className = "config-view__empty";
      empty.textContent = "No folders found. Add folders via Manage Files first.";
      folderSection.appendChild(empty);
    } else {
      for (const folder of this._folders) {
        const label = document.createElement("label");
        label.className = "config-view__folder-label";
        const cb = document.createElement("input");
        cb.type = "checkbox";
        cb.checked = true;
        cb.addEventListener("change", () => this._validate());
        this._folderChecks.set(folder.id, cb);
        label.appendChild(cb);
        label.appendChild(document.createTextNode(folder.name));
        folderSection.appendChild(label);
      }
    }
    body.appendChild(folderSection);

    // Footer
    const footer = document.createElement("div");
    footer.className = "config-view__footer";

    const cancelBtn = document.createElement("button");
    cancelBtn.className = "btn btn--ghost";
    cancelBtn.textContent = "Cancel";
    cancelBtn.addEventListener("click", () => callbacks.onCancel());

    this._startBtn = document.createElement("button");
    this._startBtn.className = "btn btn--primary";
    this._startBtn.textContent = "Start Matching";
    this._startBtn.addEventListener("click", () => {
      const folderIds = [...this._folderChecks.entries()]
        .filter(([, cb]) => cb.checked)
        .map(([id]) => id);
      callbacks.onStart([...this._outputColNumbers], folderIds);
    });

    footer.appendChild(cancelBtn);
    footer.appendChild(this._startBtn);
    this._el.appendChild(footer);
  }

  private _makeOutputSelect(keyIndex: number): HTMLSelectElement {
    const sel = document.createElement("select");
    sel.className = "config-view__col-select";

    for (const col of this._outputColumns) {
      const opt = document.createElement("option");
      opt.value = String(col.colNumber);
      opt.textContent = col.header;
      if (col.colNumber === this._outputColNumbers[keyIndex]) opt.selected = true;
      sel.appendChild(opt);
    }

    sel.addEventListener("change", () => {
      this._outputColNumbers[keyIndex] = Number(sel.value);
    });

    return sel;
  }

  private _validate(): void {
    const hasKeys = this._keyColumns.length > 0;
    const hasOutputs = this._outputColumns.length > 0;
    const hasFolders = [...this._folderChecks.values()].some((cb) => cb.checked);
    this._startBtn.disabled = !(hasKeys && hasOutputs && hasFolders);
  }

  get element(): HTMLElement {
    return this._el;
  }

  remove(): void {
    this._el.remove();
  }
}
