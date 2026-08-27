import { SearchResultsPanel } from "./search-results-panel.js";
import { normalizeSearchQuery } from "../viewer/pdf-text-searcher.js";
import { cleanAutoInsertedSearchQuery } from "@doculink/shared";
import type { SearchMatch } from "../../types/index.js";

const DEBOUNCE_MS = 250;

export class SearchBar {
  readonly element: HTMLElement;

  private readonly _input: HTMLInputElement;
  private readonly _clearButton: HTMLButtonElement;
  private readonly _searchButton: HTMLButtonElement;
  private readonly _resultsPanel: SearchResultsPanel;
  private _debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private _resultsDismissed = false;
  private _submittedQuery = "";

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
    this._input.placeholder = "Indexing documents…";
    this._input.disabled = true;
    this._input.addEventListener("input", () => this._handleInput());
    this._input.addEventListener("keydown", (e) => {
      if (e.key !== "Enter") return;
      e.preventDefault();
      this._submitQuery();
    });

    this._clearButton = document.createElement("button");
    this._clearButton.className = "search-bar__action search-bar__clear";
    this._clearButton.type = "button";
    this._clearButton.title = "Clear search";
    this._clearButton.setAttribute("aria-label", "Clear search");
    this._clearButton.textContent = "\u00d7";
    this._clearButton.hidden = true;
    this._clearButton.addEventListener("click", () => this._clearQuery());

    this._searchButton = document.createElement("button");
    this._searchButton.className = "search-bar__action search-bar__submit";
    this._searchButton.type = "button";
    this._searchButton.title = "Search";
    this._searchButton.setAttribute("aria-label", "Search");
    this._searchButton.append(SearchBar._createSearchIcon());
    this._searchButton.addEventListener("click", () => this._submitQuery());

    this._resultsPanel = new SearchResultsPanel();
    this._resultsPanel.onMatchClicked((match) => {
      this._resultsDismissed = true;
      this._resultsPanel.hide();
      for (const cb of this._onMatchClickedCallbacks) cb(match);
    });
    this._resultsPanel.onShowMore(() => {
      for (const cb of this._onShowMoreCallbacks) cb();
    });

    this.element.append(
      this._input,
      this._clearButton,
      this._searchButton,
      this._resultsPanel.element,
    );
    this._updateActions();

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
      if (
        shouldRefresh
        && this._submittedQuery
        && this._submittedQuery === normalizeSearchQuery(this._input.value)
      ) {
        this._resultsDismissed = false;
        this._emitQuery(this._submittedQuery);
      }
      this._showResults();
    };

    this._input.addEventListener("focus", reopenIfDismissed);
    this._input.addEventListener("click", reopenIfDismissed);
  }

  enable(): void {
    this._input.disabled = false;
    this._input.placeholder = "Search document…";
    this._updateActions();
  }

  disable(): void {
    this._input.disabled = true;
    this._input.placeholder = "Indexing documents…";
    this._resultsPanel.clearResults();
    this._updateActions();
  }

  getQuery(): string {
    return this._input.value;
  }

  getSubmittedQuery(): string {
    return this._submittedQuery;
  }

  setQuery(query: string): void {
    this._cancelDebounce();

    this._input.value = cleanAutoInsertedSearchQuery(query);
    this._resultsDismissed = false;
    this._submittedQuery = "";
    this._updateActions();

    // A host-filled cell value is only staged. Publishing an empty query cancels
    // any active full-search session and clears results from the previous cell;
    // the viewer may still render a lightweight visible-page highlight preview.
    this._emitQuery("");
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
    if (!this._resultsPanel.hasContent()) return;

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
    this._updateActions();

    this._cancelDebounce();

    this._debounceTimer = setTimeout(() => {
      this._debounceTimer = null;
      this._emitCurrentQuery();
    }, DEBOUNCE_MS);
  }

  private _emitCurrentQuery(): void {
    const query = normalizeSearchQuery(this._input.value);
    this._submittedQuery = query;
    this._emitQuery(query);
  }

  private _emitQuery(query: string): void {
    for (const cb of this._onQueryCallbacks) cb(query);
  }

  private _submitQuery(): void {
    if (this._input.disabled) return;
    this._cancelDebounce();
    this._resultsDismissed = false;
    this._emitCurrentQuery();
  }

  private _clearQuery(): void {
    if (this._input.disabled) return;
    this._cancelDebounce();
    this._input.value = "";
    this._resultsDismissed = false;
    this._updateActions();
    this._emitCurrentQuery();
    this._input.focus();
  }

  private _cancelDebounce(): void {
    if (this._debounceTimer === null) return;
    clearTimeout(this._debounceTimer);
    this._debounceTimer = null;
  }

  private _updateActions(): void {
    const disabled = this._input.disabled;
    const hasQuery = normalizeSearchQuery(this._input.value).length > 0;
    this._clearButton.hidden = !this._input.value;
    this._clearButton.disabled = disabled;
    this._searchButton.disabled = disabled || !hasQuery;
  }

  private static _createSearchIcon(): SVGSVGElement {
    const namespace = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(namespace, "svg");
    svg.setAttribute("viewBox", "0 0 20 20");
    svg.setAttribute("aria-hidden", "true");

    const circle = document.createElementNS(namespace, "circle");
    circle.setAttribute("cx", "8.5");
    circle.setAttribute("cy", "8.5");
    circle.setAttribute("r", "5.5");

    const handle = document.createElementNS(namespace, "path");
    handle.setAttribute("d", "M12.5 12.5 17 17");

    svg.append(circle, handle);
    return svg;
  }
}
