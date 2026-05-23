import { SearchResultsPanel } from "./search-results-panel.js";
import { normalizeSearchQuery } from "../viewer/pdf-text-searcher.js";
import type { SearchMatch } from "../../types/index.js";

const DEBOUNCE_MS = 250;

export class SearchBar {
  readonly element: HTMLElement;

  private readonly _input: HTMLInputElement;
  private readonly _resultsPanel: SearchResultsPanel;
  private _debounceTimer: ReturnType<typeof setTimeout> | null = null;

  private readonly _onQueryCallbacks: Array<(query: string) => void> = [];
  private readonly _onMatchClickedCallbacks: Array<(match: SearchMatch) => void> = [];
  private _mousedownOutside = false;

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
      this._resultsPanel.hide();
      for (const cb of this._onMatchClickedCallbacks) cb(match);
    });

    this.element.append(this._input, this._resultsPanel.element);

    document.addEventListener("mousedown", (e) => {
      this._mousedownOutside = !this.element.contains(e.target as Node);
    });

    document.addEventListener("click", (e) => {
      if (this._mousedownOutside && !this.element.contains(e.target as Node)) {
        this._resultsPanel.hide();
      }
    });

    this._input.addEventListener("focus", () => {
      if (this._resultsPanel.hasResults()) {
        this._resultsPanel.show();
      }
    });
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

  onQuery(cb: (query: string) => void): void {
    this._onQueryCallbacks.push(cb);
  }

  onMatchClicked(cb: (match: SearchMatch) => void): void {
    this._onMatchClickedCallbacks.push(cb);
  }

  setResults(matches: SearchMatch[]): void {
    this._resultsPanel.setResults(matches);
  }

  clearResults(): void {
    this._resultsPanel.clearResults();
  }

  private _handleInput(): void {
    if (this._debounceTimer !== null) {
      clearTimeout(this._debounceTimer);
    }

    this._debounceTimer = setTimeout(() => {
      this._debounceTimer = null;
      const query = normalizeSearchQuery(this._input.value);
      for (const cb of this._onQueryCallbacks) cb(query);
    }, DEBOUNCE_MS);
  }
}
