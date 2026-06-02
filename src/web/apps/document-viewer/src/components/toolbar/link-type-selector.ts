import type { LinkType } from "../../types/index.js";

type LinkTypeChangedCallback = (type: LinkType) => void;

const TYPES: { value: LinkType; label: string; title: string }[] = [
  { value: "auto", label: "Auto",  title: "Auto: formats numbers (strips commas, converts parentheses to negatives)" },
  { value: "raw",  label: "Raw",   title: "Raw: writes extracted text exactly as-is" },
  { value: "sum",  label: "Sum",   title: "Sum: generates an Excel formula summing all numbers in the selection" },
];

export class LinkTypeSelector {
  readonly element: HTMLElement;

  private _current: LinkType = "auto";
  private _callbacks: LinkTypeChangedCallback[] = [];
  private _buttons: Map<LinkType, HTMLButtonElement> = new Map();

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "link-type-selector";

    for (const { value, label, title } of TYPES) {
      const btn = document.createElement("button");
      btn.className = "link-type-selector__btn";
      btn.textContent = label;
      btn.title = title;
      btn.dataset["type"] = value;
      btn.addEventListener("click", () => this._select(value));
      this._buttons.set(value, btn);
      this.element.appendChild(btn);
    }

    this._updateActive();
  }

  getLinkType(): LinkType {
    return this._current;
  }

  onLinkTypeChange(cb: LinkTypeChangedCallback): void {
    this._callbacks.push(cb);
  }

  private _select(type: LinkType): void {
    if (this._current === type) return;
    this._current = type;
    this._updateActive();
    for (const cb of this._callbacks) cb(type);
  }

  private _updateActive(): void {
    for (const [type, btn] of this._buttons) {
      btn.classList.toggle("link-type-selector__btn--active", type === this._current);
    }
  }
}
