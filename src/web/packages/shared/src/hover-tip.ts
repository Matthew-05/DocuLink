/**
 * A floating tip that explains whatever sits under the cursor.
 *
 * General purpose: the caller supplies a `resolve` answering "what is at this
 * point", and this class owns the element, its placement, and closing again
 * reliably. It says nothing about what a tip is for, so the same component
 * serves a diagnostic overlay and a user-facing hint; the wording, and how
 * technical it is, belongs entirely to the content the caller builds.
 *
 * It hit-tests rather than relying on `:hover` or `title`, which suits any
 * layer that cannot report its own hover -- one that is `pointer-events: none`
 * so it never takes a click from the content beneath it, or one drawn to a
 * canvas and so having no elements at all.
 *
 * Closing is the hard part. A pointer can stop producing moves while a tip is
 * open in several ways, and the page can move while the pointer holds still, so
 * this listens on the document rather than on any one element and re-resolves
 * whenever content can shift underneath.
 */
export interface HoverTipContent {
  /** Identity of the subject. The tip only rebuilds when this changes. */
  key: string;
  /** Short heading, emphasised. */
  label: string;
  /** One sentence saying what the label means. */
  detail: string;
  /** The literal text the tip is about, set monospaced. Optional. */
  code?: string;
  /** Name/value rows for the technical detail behind the summary. Optional. */
  fields?: ReadonlyArray<{ name: string; value: string }>;
  /** Written to `data-variant` so a stylesheet can accent per category. */
  variant?: string;
}

export interface HoverTipOptions {
  /**
   * Extra class on the root, for styling one use differently from another.
   * `doculink-hover-tip` is always present.
   */
  className?: string;
  /** What sits at this client point, or null. Called on every pointer move. */
  resolve: (clientX: number, clientY: number) => HoverTipContent | null;
}

const ROOT_CLASS = "doculink-hover-tip";
/** Clear of the cursor without putting the tip under the pointer. */
const CURSOR_OFFSET = 14;
const EDGE_MARGIN = 4;

export class HoverTip {
  private readonly _resolve: HoverTipOptions["resolve"];
  private readonly _className: string;
  private _element: HTMLDivElement | undefined;
  private _labelNode: HTMLSpanElement | undefined;
  private _detailNode: HTMLSpanElement | undefined;
  private _codeNode: HTMLSpanElement | undefined;
  private _fieldsNode: HTMLDivElement | undefined;
  private _shownFor = "";
  private _width = 0;
  private _height = 0;
  private _pointerX = 0;
  private _pointerY = 0;
  private _pointerKnown = false;
  private _enabled = false;

  constructor(options: HoverTipOptions) {
    this._resolve = options.resolve;
    this._className = options.className ?? "";
    document.addEventListener("pointermove", this._onPointerMove);
    // The pointer left the window: to another application, or to a host surface
    // outside this document.
    document.documentElement.addEventListener("mouseleave", this.hide);
    window.addEventListener("blur", this.hide);
    // A press begins a click or a drag; the tip should get out of the way.
    document.addEventListener("pointerdown", this.hide, true);
    document.addEventListener("pointercancel", this.hide, true);
    // The cursor can hold still while content moves out from under it. Capture,
    // so scrolling inside any nested scroller counts.
    document.addEventListener("scroll", this.refresh, true);
    window.addEventListener("resize", this.hide);
  }

  /** Whether the tip may open. Disabling closes it. */
  setEnabled(enabled: boolean): void {
    this._enabled = enabled;
    if (!enabled) this.hide();
  }

  get enabled(): boolean { return this._enabled; }

  /**
   * Re-ask `resolve` at the last known pointer position. Call after anything
   * that moves content without moving the pointer -- a zoom, a re-render, a
   * page change -- so an open tip closes instead of describing stale content.
   */
  readonly refresh = (): void => {
    if (!this._element || this._element.hidden) return;
    if (!this._enabled || !this._pointerKnown) { this.hide(); return; }
    this._apply(this._resolve(this._pointerX, this._pointerY));
  };

  readonly hide = (): void => {
    if (this._element) this._element.hidden = true;
    this._shownFor = "";
  };

  /** Removes the listeners and the element. */
  dispose(): void {
    document.removeEventListener("pointermove", this._onPointerMove);
    document.documentElement.removeEventListener("mouseleave", this.hide);
    window.removeEventListener("blur", this.hide);
    document.removeEventListener("pointerdown", this.hide, true);
    document.removeEventListener("pointercancel", this.hide, true);
    document.removeEventListener("scroll", this.refresh, true);
    window.removeEventListener("resize", this.hide);
    this._element?.remove();
    this._element = undefined;
    this._shownFor = "";
  }

  private readonly _onPointerMove = (event: PointerEvent): void => {
    if (!this._enabled) return;
    this._pointerX = event.clientX;
    this._pointerY = event.clientY;
    this._pointerKnown = true;
    this._apply(this._resolve(event.clientX, event.clientY));
  };

  private _apply(content: HoverTipContent | null): void {
    if (!content) { this.hide(); return; }
    const element = this._element ?? this._create();
    if (this._shownFor !== content.key) {
      this._shownFor = content.key;
      this._labelNode!.textContent = content.label;
      this._detailNode!.textContent = content.detail;
      this._codeNode!.textContent = content.code ?? "";
      this._codeNode!.hidden = !content.code;
      this._renderFields(content.fields);
      if (content.variant) element.dataset["variant"] = content.variant;
      else delete element.dataset["variant"];
      element.hidden = false;
      // Measured once per subject. Reading it on every move would force a
      // layout between the style writes below, on a continuous event.
      this._width = element.offsetWidth;
      this._height = element.offsetHeight;
    }
    element.hidden = false;
    this._position(element);
  }

  private _renderFields(fields: HoverTipContent["fields"]): void {
    const node = this._fieldsNode!;
    node.replaceChildren();
    node.hidden = !fields || fields.length === 0;
    for (const field of fields ?? []) {
      const name = document.createElement("span");
      name.className = `${ROOT_CLASS}__field-name`;
      name.textContent = field.name;
      const value = document.createElement("span");
      value.className = `${ROOT_CLASS}__field-value`;
      value.textContent = field.value;
      node.append(name, value);
    }
  }

  /** Placed beside the cursor, flipped near an edge so it stays on screen. */
  private _position(element: HTMLDivElement): void {
    const overflowsRight = this._pointerX + CURSOR_OFFSET + this._width > window.innerWidth;
    const overflowsBottom = this._pointerY + CURSOR_OFFSET + this._height > window.innerHeight;
    const left = overflowsRight
      ? this._pointerX - CURSOR_OFFSET - this._width
      : this._pointerX + CURSOR_OFFSET;
    const top = overflowsBottom
      ? this._pointerY - CURSOR_OFFSET - this._height
      : this._pointerY + CURSOR_OFFSET;
    element.style.left = `${Math.max(EDGE_MARGIN, left)}px`;
    element.style.top = `${Math.max(EDGE_MARGIN, top)}px`;
  }

  private _create(): HTMLDivElement {
    const element = document.createElement("div");
    element.className = this._className ? `${ROOT_CLASS} ${this._className}` : ROOT_CLASS;
    element.hidden = true;
    element.setAttribute("role", "tooltip");
    this._labelNode = document.createElement("span");
    this._labelNode.className = `${ROOT_CLASS}__label`;
    this._detailNode = document.createElement("span");
    this._detailNode.className = `${ROOT_CLASS}__detail`;
    this._codeNode = document.createElement("span");
    this._codeNode.className = `${ROOT_CLASS}__code`;
    this._fieldsNode = document.createElement("div");
    this._fieldsNode.className = `${ROOT_CLASS}__fields`;
    this._fieldsNode.hidden = true;
    element.append(this._labelNode, this._detailNode, this._codeNode, this._fieldsNode);
    // On <body>, so a tip is never clipped by an overflow ancestor and never
    // scrolls out of step with the cursor it is anchored to.
    document.body.appendChild(element);
    this._element = element;
    return element;
  }
}
