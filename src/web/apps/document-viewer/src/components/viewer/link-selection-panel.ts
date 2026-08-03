import type { LinkSelectionEntry } from "../../types/index.js";

/**
 * Floating panel listing every linked cell in the current Excel selection.
 *
 * Shown only for multi-cell selections (two or more linked cells); a single
 * linked cell is already communicated by rectangle navigation and highlight.
 * The document name is rendered only when the selection spans more than one PDF.
 */
export class LinkSelectionPanel {
  static readonly MIN_ENTRIES = 2;

  readonly element: HTMLElement;

  private readonly _callbacks: Array<(entry: LinkSelectionEntry) => void> = [];
  private _entries: LinkSelectionEntry[] = [];
  private _activeId: string | null = null;

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "link-selection-panel";
    this.element.hidden = true;
    this.element.addEventListener("click", (e) => this._handleClick(e));
  }

  /** Registers a callback invoked when the user clicks a listed link. */
  onEntryClicked(cb: (entry: LinkSelectionEntry) => void): void {
    this._callbacks.push(cb);
  }

  /**
   * Replaces the listed entries. Fewer than {@link LinkSelectionPanel.MIN_ENTRIES}
   * entries hides the panel.
   */
  setEntries(entries: LinkSelectionEntry[]): void {
    this._entries = entries;
    if (this._activeId !== null && !entries.some((e) => e.id === this._activeId)) {
      this._activeId = null;
    }
    this._render();
  }

  clear(): void {
    this.setEntries([]);
  }

  /** Marks one listed link as the one currently shown in the viewer. */
  setActiveEntry(id: string | null): void {
    if (this._activeId === id) return;
    this._activeId = id;
    this._render();
  }

  private _render(): void {
    this.element.replaceChildren();

    if (this._entries.length < LinkSelectionPanel.MIN_ENTRIES) {
      this.element.hidden = true;
      return;
    }

    this.element.hidden = false;

    const summary = document.createElement("div");
    summary.className = "link-selection-panel__summary";
    summary.textContent = `${this._entries.length} linked cells selected`;
    this.element.appendChild(summary);

    const list = document.createElement("div");
    list.className = "link-selection-panel__list";

    const showDocument = new Set(this._entries.map((e) => e.pdfId)).size > 1;
    for (const entry of this._entries) {
      list.appendChild(this._createItem(entry, showDocument));
    }

    this.element.appendChild(list);
  }

  private _createItem(entry: LinkSelectionEntry, showDocument: boolean): HTMLElement {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "link-selection-panel__item";
    if (entry.id === this._activeId) {
      item.classList.add("link-selection-panel__item--active");
    }
    item.dataset["entryId"] = entry.id;

    const value = document.createElement("span");
    value.className = "link-selection-panel__value";
    value.textContent = entry.value || "(empty)";
    if (!entry.value) value.classList.add("link-selection-panel__value--empty");

    const meta = document.createElement("span");
    meta.className = "link-selection-panel__meta";
    meta.textContent = showDocument
      ? `${entry.pdfName || "Untitled"} · p.${entry.page + 1}`
      : `p.${entry.page + 1}`;
    meta.title = meta.textContent;

    item.append(value, meta);
    return item;
  }

  private _handleClick(e: MouseEvent): void {
    const target = e.target as HTMLElement | null;
    const entryId = target?.closest<HTMLElement>("[data-entry-id]")?.dataset["entryId"];
    if (!entryId) return;

    const entry = this._entries.find((candidate) => candidate.id === entryId);
    if (!entry) return;

    this.setActiveEntry(entry.id);
    for (const cb of this._callbacks) cb(entry);
  }
}
