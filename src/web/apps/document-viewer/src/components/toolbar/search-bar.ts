export class SearchBar {
  readonly element: HTMLElement;

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "search-bar";

    const input = document.createElement("input");
    input.className = "search-bar__input";
    input.type = "search";
    input.placeholder = "Search PDF…";
    input.disabled = true;

    this.element.appendChild(input);
  }
}
