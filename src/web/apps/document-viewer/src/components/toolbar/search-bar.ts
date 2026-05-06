export class SearchBar {
  readonly element: HTMLElement;

  private _input: HTMLInputElement;

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "search-bar";

    this._input = document.createElement("input");
    this._input.className = "search-bar__input";
    this._input.type = "search";
    this._input.placeholder = "Search PDF…";
    this._input.disabled = true;

    this.element.appendChild(this._input);
  }

  setEnabled(enabled: boolean): void {
    this._input.disabled = !enabled;
  }
}
