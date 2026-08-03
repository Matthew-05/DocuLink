import type { LinkSelectionEntry } from "../../types/index.js";

/**
 * Floating panel listing every linked rectangle in the current Excel selection.
 *
 * Shown once the selection covers two or more rectangles — several linked cells,
 * or a single sum cell built from several rectangles. A lone rectangle is already
 * communicated by navigation and highlight, so the panel stays hidden.
 *
 * Rows are grouped under a cell header when the selection spans more than one
 * cell, and carry their document name only when it spans more than one PDF.
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

    const groups = groupByCell(this._entries);
    const showDocument = new Set(this._entries.map((e) => e.pdfId)).size > 1;
    // With a single cell selected the summary already names it, so no heading.
    const showCellHeadings = groups.size > 1;

    const summary = document.createElement("div");
    summary.className = "link-selection-panel__summary";
    summary.textContent = this._summaryText(groups);
    summary.title = summary.textContent;
    this.element.appendChild(summary);

    const list = document.createElement("div");
    list.className = "link-selection-panel__list";

    for (const [, groupEntries] of groups) {
      const section = document.createElement("div");
      section.className = "link-selection-panel__section";

      // A cell only earns a heading when it contributes several rectangles — i.e. a
      // sum cell, where the rows are parts and the heading carries their total.
      // One-rectangle cells render as plain rows, so an ordinary selection of
      // several linked cells stays a flat list.
      if (showCellHeadings && groupEntries.length > 1) {
        section.appendChild(this._createHeading(groupEntries[0]!));
      }

      for (const entry of groupEntries) {
        section.appendChild(this._createItem(entry, showDocument));
      }

      list.appendChild(section);
    }

    this.element.appendChild(list);
  }

  /**
   * Summarizes cells, not rectangles, unless a single sum cell is selected — then
   * there is no heading to carry the cell and its total, so the summary does.
   */
  private _summaryText(groups: Map<string, LinkSelectionEntry[]>): string {
    if (groups.size === 1 && this._entries.length > 1) {
      const first = this._entries[0]!;
      const cell = first.cellAddress || "selected cell";
      return first.cellValue
        ? `${this._entries.length} rectangles · ${cell} = ${first.cellValue}`
        : `${this._entries.length} rectangles · ${cell}`;
    }

    return `${groups.size} linked cells selected`;
  }

  private _createHeading(entry: LinkSelectionEntry): HTMLElement {
    const heading = document.createElement("div");
    heading.className = "link-selection-panel__heading";

    const address = document.createElement("span");
    address.className = "link-selection-panel__address";
    address.textContent = entry.cellAddress;

    heading.append(address);

    if (entry.cellValue) {
      const total = document.createElement("span");
      total.className = "link-selection-panel__total";
      total.textContent = entry.cellValue;
      heading.append(total);
    }

    heading.title = entry.cellValue
      ? `${entry.cellAddress} · ${entry.cellValue}`
      : entry.cellAddress;

    return heading;
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

    if (entry.valueCount > 1) {
      // One rectangle captured several numbers — show what it contributes, then how
      // many numbers went into it rather than listing them all.
      const count = document.createElement("span");
      count.className = "link-selection-panel__count";
      count.textContent = `${entry.valueCount} values`;
      value.append(" ", count);
    }

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

/** Groups entries by linked cell, preserving the host's ordering. */
function groupByCell(entries: LinkSelectionEntry[]): Map<string, LinkSelectionEntry[]> {
  const groups = new Map<string, LinkSelectionEntry[]>();
  for (const entry of entries) {
    const list = groups.get(entry.cellAddress) ?? [];
    list.push(entry);
    groups.set(entry.cellAddress, list);
  }
  return groups;
}
