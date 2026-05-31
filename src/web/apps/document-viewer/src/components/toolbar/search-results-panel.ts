import type { SearchMatch } from "../../types/index.js";

export class SearchResultsPanel {
  readonly element: HTMLElement;

  private readonly _callbacks: Array<(match: SearchMatch) => void> = [];
  private _matches: SearchMatch[] = [];

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "search-results-panel";
    this.element.hidden = true;
  }

  onMatchClicked(cb: (match: SearchMatch) => void): void {
    this._callbacks.push(cb);
  }

  setResults(matches: SearchMatch[]): void {
    this._matches = matches;
    this._render();
  }

  clearResults(): void {
    this._matches = [];
    this.element.replaceChildren();
    this.element.hidden = true;
  }

  hide(): void {
    this.element.hidden = true;
  }

  show(): void {
    if (this._matches.length > 0) {
      this.element.hidden = false;
    }
  }

  hasResults(): boolean {
    return this._matches.length > 0;
  }

  private _render(): void {
    this.element.replaceChildren();

    if (this._matches.length === 0) {
      this.element.hidden = true;
      return;
    }

    this.element.hidden = false;

    const grouped = groupByPdf(this._matches);
    for (const [pdfName, pdfMatches] of grouped) {
      const section = document.createElement("div");
      section.className = "search-results-panel__section";

      const heading = document.createElement("div");
      heading.className = "search-results-panel__heading";
      heading.textContent = pdfName;
      section.appendChild(heading);

      const byPage = groupByPage(pdfMatches);
      for (const [pageIndex, pageMatches] of byPage) {
        for (const match of pageMatches) {
          section.appendChild(this._createItem(match, pageIndex));
        }
      }

      this.element.appendChild(section);
    }
  }

  private _createItem(match: SearchMatch, pageIndex: number): HTMLElement {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "search-results-panel__item";

    const pageLabel = document.createElement("span");
    pageLabel.className = "search-results-panel__page";
    pageLabel.textContent = `${pageIndex + 1}`;

    const context = document.createElement("span");
    context.className = "search-results-panel__context";
    context.append(this._buildContextFragment(match));

    item.append(context, pageLabel);
    item.addEventListener("click", () => {
      for (const cb of this._callbacks) cb(match);
    });

    return item;
  }

  private _buildContextFragment(match: SearchMatch): DocumentFragment {
    const frag = document.createDocumentFragment();
    const { start, end } = match.matchInContext;
    const before = match.contextText.slice(0, start);
    const hit    = match.contextText.slice(start, end);
    const after  = match.contextText.slice(end);

    if (before) frag.append(document.createTextNode(before));

    const mark = document.createElement("mark");
    mark.className = "search-results-panel__mark";
    mark.textContent = hit;
    frag.append(mark);

    if (after) frag.append(document.createTextNode(after));

    return frag;
  }
}

function groupByPdf(matches: SearchMatch[]): Map<string, SearchMatch[]> {
  const map = new Map<string, SearchMatch[]>();
  for (const match of matches) {
    const list = map.get(match.pdfName) ?? [];
    list.push(match);
    map.set(match.pdfName, list);
  }
  return map;
}

function groupByPage(matches: SearchMatch[]): Map<number, SearchMatch[]> {
  const map = new Map<number, SearchMatch[]>();
  for (const match of matches) {
    const list = map.get(match.pageIndex) ?? [];
    list.push(match);
    map.set(match.pageIndex, list);
  }
  return new Map([...map.entries()].sort(([a], [b]) => a - b));
}
