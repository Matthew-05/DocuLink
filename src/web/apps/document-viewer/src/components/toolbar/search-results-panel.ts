import type { SearchMatch } from "../../types/index.js";

export interface SearchResultsPanelState {
  matches: SearchMatch[];
  hasMore: boolean;
  canLoadMore?: boolean;
}

export function getSearchResultsSummary(matchCount: number, hasMore: boolean): string {
  if (matchCount === 0) return hasMore ? "Searching..." : "No results";
  if (hasMore) return `${matchCount}+ results`;
  return `${matchCount} result${matchCount === 1 ? "" : "s"}`;
}

export class SearchResultsPanel {
  readonly element: HTMLElement;

  private readonly _callbacks: Array<(match: SearchMatch) => void> = [];
  private readonly _showMoreCallbacks: Array<() => void> = [];
  private _matches: SearchMatch[] = [];
  private _hasMore = false;
  private _canLoadMore = false;
  private _hasSearchState = false;

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "search-results-panel";
    this.element.hidden = true;
    this.element.addEventListener("click", (e) => this._handleClick(e));
  }

  onMatchClicked(cb: (match: SearchMatch) => void): void {
    this._callbacks.push(cb);
  }

  onShowMore(cb: () => void): void {
    this._showMoreCallbacks.push(cb);
  }

  setResults(state: SearchResultsPanelState): void {
    this._matches = state.matches;
    this._hasMore = state.hasMore;
    this._canLoadMore = state.canLoadMore ?? state.hasMore;
    this._hasSearchState = true;
    this._render();
  }

  clearResults(): void {
    this._matches = [];
    this._hasMore = false;
    this._canLoadMore = false;
    this._hasSearchState = false;
    this.element.replaceChildren();
    this.element.hidden = true;
  }

  hide(): void {
    this.element.hidden = true;
  }

  show(): void {
    if (this._hasSearchState) {
      this.element.hidden = false;
    }
  }

  hasContent(): boolean {
    return this._hasSearchState;
  }

  private _render(): void {
    this.element.replaceChildren();

    this.element.hidden = false;

    const summary = document.createElement("div");
    summary.className = "search-results-panel__summary";
    summary.textContent = getSearchResultsSummary(this._matches.length, this._hasMore);
    this.element.appendChild(summary);

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

    if (this._canLoadMore) {
      const more = document.createElement("button");
      more.type = "button";
      more.className = "search-results-panel__more";
      more.dataset["action"] = "show-more";
      more.textContent = "Show more";
      this.element.appendChild(more);
    }
  }

  private _createItem(match: SearchMatch, pageIndex: number): HTMLElement {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "search-results-panel__item";

    const pageLabel = document.createElement("span");
    pageLabel.className = "search-results-panel__page";
    pageLabel.textContent = `Page ${pageIndex + 1}`;

    const context = document.createElement("span");
    context.className = "search-results-panel__context";
    context.append(this._buildContextFragment(match));

    item.append(context, pageLabel);
    item.dataset["matchId"] = match.id;

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

  private _handleClick(e: MouseEvent): void {
    const target = e.target as HTMLElement | null;
    const showMore = target?.closest<HTMLElement>("[data-action='show-more']");
    if (showMore) {
      for (const cb of this._showMoreCallbacks) cb();
      return;
    }

    const item = target?.closest<HTMLElement>("[data-match-id]");
    const matchId = item?.dataset["matchId"];
    if (!matchId) return;

    const match = this._matches.find((candidate) => candidate.id === matchId);
    if (!match) return;

    for (const cb of this._callbacks) cb(match);
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
