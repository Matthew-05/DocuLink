export interface ColumnSelectOption {
  value: number;
  label: string;
}

export interface ColumnSelectCallbacks {
  onChange: (value: number) => void;
  /** Fired as the pointer or keyboard moves over an option, before any commit. */
  onOptionHover?: (value: number) => void;
  /** Fired when hover ends: list closed, pointer left the list, or focus lost. */
  onHoverEnd?: () => void;
}

/**
 * Dropdown that reports which option the user is pointing at.
 *
 * A native `<select>` cannot do this: its popup is drawn by the OS, so the
 * `<option>` elements never receive mouse events. The listbox here is real DOM,
 * which is the whole reason this component exists.
 */
export class ColumnSelect {
  private readonly _el: HTMLElement;
  private readonly _button: HTMLButtonElement;
  private readonly _list: HTMLElement;
  private readonly _callbacks: ColumnSelectCallbacks;
  private _options: ColumnSelectOption[];
  private _value: number;
  private _open = false;
  private _activeIndex = -1;
  private _hoverActive = false;

  private readonly _onDocPointerDown = (ev: Event): void => {
    if (!this._el.contains(ev.target as Node)) this.close();
  };

  constructor(
    options: ColumnSelectOption[],
    value: number,
    callbacks: ColumnSelectCallbacks,
  ) {
    this._options = options;
    this._value = value;
    this._callbacks = callbacks;

    this._el = document.createElement("div");
    this._el.className = "column-select";

    this._button = document.createElement("button");
    this._button.type = "button";
    this._button.className = "column-select__button";
    this._button.setAttribute("aria-haspopup", "listbox");
    this._button.setAttribute("aria-expanded", "false");
    this._button.addEventListener("click", () => this.toggle());
    this._button.addEventListener("keydown", (ev) => this._onButtonKeyDown(ev));
    this._el.appendChild(this._button);

    this._list = document.createElement("div");
    this._list.className = "column-select__list";
    this._list.setAttribute("role", "listbox");
    this._list.hidden = true;
    this._list.addEventListener("mouseleave", () => this._endHover());
    this._el.appendChild(this._list);

    this._renderButton();
    this._renderList();
  }

  get element(): HTMLElement {
    return this._el;
  }

  get value(): number {
    return this._value;
  }

  setOptions(options: ColumnSelectOption[], value: number): void {
    this._options = options;
    this._value = value;
    this._renderButton();
    this._renderList();
  }

  setError(hasError: boolean): void {
    this._button.classList.toggle("column-select__button--error", hasError);
  }

  toggle(): void {
    if (this._open) this.close();
    else this.open();
  }

  open(): void {
    if (this._open || this._options.length === 0) return;
    this._open = true;
    this._list.hidden = false;
    this._button.setAttribute("aria-expanded", "true");
    document.addEventListener("pointerdown", this._onDocPointerDown, true);

    const selectedIndex = this._options.findIndex((o) => o.value === this._value);
    this._setActiveIndex(selectedIndex >= 0 ? selectedIndex : 0);
  }

  close(): void {
    if (!this._open) return;
    this._open = false;
    this._list.hidden = true;
    this._button.setAttribute("aria-expanded", "false");
    document.removeEventListener("pointerdown", this._onDocPointerDown, true);
    this._setActiveIndex(-1);
    this._endHover();
  }

  remove(): void {
    document.removeEventListener("pointerdown", this._onDocPointerDown, true);
    this._el.remove();
  }

  private _renderButton(): void {
    const selected = this._options.find((o) => o.value === this._value);
    this._button.textContent = selected?.label ?? "";
  }

  private _renderList(): void {
    this._list.innerHTML = "";

    this._options.forEach((option, index) => {
      const item = document.createElement("div");
      item.className = "column-select__option";
      item.setAttribute("role", "option");
      item.setAttribute("aria-selected", String(option.value === this._value));
      item.dataset.index = String(index);
      item.textContent = option.label;

      item.addEventListener("mouseenter", () => this._setActiveIndex(index));
      item.addEventListener("click", () => this._commit(index));

      this._list.appendChild(item);
    });
  }

  private _setActiveIndex(index: number): void {
    const items = this._list.children;
    for (let i = 0; i < items.length; i++) {
      items[i]?.classList.toggle("column-select__option--active", i === index);
    }

    this._activeIndex = index;
    if (index < 0) return;

    (items[index] as HTMLElement | undefined)?.scrollIntoView({ block: "nearest" });

    const option = this._options[index];
    if (!option) return;
    this._hoverActive = true;
    this._callbacks.onOptionHover?.(option.value);
  }

  private _endHover(): void {
    if (!this._hoverActive) return;
    this._hoverActive = false;
    this._callbacks.onHoverEnd?.();
  }

  private _commit(index: number): void {
    const option = this._options[index];
    if (option) {
      this._value = option.value;
      this._renderButton();
      this._renderList();
      this._callbacks.onChange(option.value);
    }
    this.close();
    this._button.focus();
  }

  private _onButtonKeyDown(ev: KeyboardEvent): void {
    switch (ev.key) {
      case "ArrowDown":
      case "ArrowUp": {
        ev.preventDefault();
        if (!this._open) {
          this.open();
          return;
        }
        const delta = ev.key === "ArrowDown" ? 1 : -1;
        const next = clamp(this._activeIndex + delta, 0, this._options.length - 1);
        this._setActiveIndex(next);
        break;
      }
      case "Home":
      case "End": {
        if (!this._open) return;
        ev.preventDefault();
        this._setActiveIndex(ev.key === "Home" ? 0 : this._options.length - 1);
        break;
      }
      case "Enter":
      case " ": {
        ev.preventDefault();
        if (!this._open) this.open();
        else this._commit(this._activeIndex);
        break;
      }
      case "Escape": {
        if (!this._open) return;
        ev.preventDefault();
        this.close();
        break;
      }
      case "Tab":
        this.close();
        break;
    }
  }
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}
