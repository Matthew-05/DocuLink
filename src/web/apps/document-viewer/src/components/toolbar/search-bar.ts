import { SearchResultsPanel } from "./search-results-panel.js";
import { normalizeSearchQuery } from "../viewer/pdf-text-searcher.js";
import type { SearchMatch } from "../../types/index.js";

const DEBOUNCE_MS = 250;

export class SearchBar {
  readonly element: HTMLElement;

  private readonly _input: HTMLInputElement;
  private readonly _resultsPanel: SearchResultsPanel;
  private _debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private _resultsDismissed = false;

  private readonly _onQueryCallbacks: Array<(query: string) => void> = [];
  private readonly _onMatchClickedCallbacks: Array<(match: SearchMatch) => void> = [];
  private readonly _onResultsShownCallbacks: Array<() => void> = [];
  private readonly _onShowMoreCallbacks: Array<() => void> = [];

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "search-bar";

    this._input = document.createElement("input");
    this._input.className = "search-bar__input";
    this._input.type = "search";
    this._input.placeholder = "Indexing PDFs…";
    this._input.disabled = true;
    this._input.addEventListener("input", () => this._handleInput());

    this._resultsPanel = new SearchResultsPanel();
    this._resultsPanel.onMatchClicked((match) => {
      this._resultsDismissed = true;
      this._resultsPanel.hide();
      for (const cb of this._onMatchClickedCallbacks) cb(match);
    });
    this._resultsPanel.onShowMore(() => {
      for (const cb of this._onShowMoreCallbacks) cb();
    });

    this.element.append(this._input, this._resultsPanel.element);

    document.addEventListener(
      "mousedown",
      (e) => {
        if (!this.element.contains(e.target as Node)) {
          this.hideResults();
        }
      },
      true
    );

    const reopenIfDismissed = (): void => {
      const shouldRefresh = this._resultsPanel.element.hidden || this._resultsDismissed;
      if (shouldRefresh) {
        this._resultsDismissed = false;
        this._emitCurrentQuery();
      }
      this._showResults();
    };

    this._input.addEventListener("focus", reopenIfDismissed);
    this._input.addEventListener("click", reopenIfDismissed);
  }

  enable(): void {
    this._input.disabled = false;
    this._input.placeholder = "Search PDF…";
  }

  disable(): void {
    this._input.disabled = true;
    this._input.placeholder = "Indexing PDFs…";
    this._resultsPanel.clearResults();
  }

  getQuery(): string {
    return this._input.value;
  }

  focus(): void {
    if (this._input.disabled) return;
    this._input.focus();
    this._input.select();
    this._showResults();
  }

  blur(): void {
    this._input.blur();
  }

  onQuery(cb: (query: string) => void): void {
    this._onQueryCallbacks.push(cb);
  }

  onMatchClicked(cb: (match: SearchMatch) => void): void {
    this._onMatchClickedCallbacks.push(cb);
  }

  onResultsShown(cb: () => void): void {
    this._onResultsShownCallbacks.push(cb);
  }

  onShowMore(cb: () => void): void {
    this._onShowMoreCallbacks.push(cb);
  }

  hideResults(): void {
    this._resultsDismissed = true;
    this._resultsPanel.hide();
  }

  setResults(matches: SearchMatch[], hasMore = false, canLoadMore = hasMore): void {
    const wasHidden = this._resultsPanel.element.hidden;
    this._resultsPanel.setResults({ matches, hasMore, canLoadMore });
    if (this._resultsDismissed) {
      this._resultsPanel.hide();
      return;
    }

    if (!this._resultsPanel.element.hidden && wasHidden) {
      this._notifyResultsShown();
    }
  }

  clearResults(): void {
    this._resultsPanel.clearResults();
  }

  private _showResults(): void {
    if (!this._resultsPanel.hasResults()) return;

    this._resultsDismissed = false;
    const wasHidden = this._resultsPanel.element.hidden;
    this._resultsPanel.show();
    if (wasHidden) {
      this._notifyResultsShown();
    }
  }

  private _notifyResultsShown(): void {
    for (const cb of this._onResultsShownCallbacks) cb();
  }

  private _handleInput(): void {
    this._resultsDismissed = false;

    if (this._debounceTimer !== null) {
      clearTimeout(this._debounceTimer);
    }

    this._debounceTimer = setTimeout(() => {
      this._debounceTimer = null;
      this._emitCurrentQuery();
    }, DEBOUNCE_MS);
  }

  private _emitCurrentQuery(): void {
    const query = normalizeSearchQuery(this._input.value);
    for (const cb of this._onQueryCallbacks) cb(query);
  }
}
