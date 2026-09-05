import type { FinancialValue, FsNoiseValue } from "@doculink/shared";
import type { FsValuesCache } from "../../services/fs-values-cache.js";
import type { PdfViewer } from "./pdf-viewer.js";
import { ensureOverlayLayer } from "./page-renderer.js";

const OVERLAY_CLASS = "fs-values";
const NOISE_CLASS = "fs-values-noise";

export class FsValuesOverlay {
  private _debugVisible = false;
  private _noiseVisible = false;
  private _tip: HTMLDivElement | undefined;
  private _tipFor = "";
  private _tipWidth = 0;
  private _tipHeight = 0;
  private readonly _callbacks: Array<(pdfId: string, pageIndex: number, value: FinancialValue) => void> = [];
  private readonly _viewer: PdfViewer;
  private readonly _cache: FsValuesCache;

  constructor(viewer: PdfViewer, cache: FsValuesCache) {
    this._viewer = viewer;
    this._cache = cache;
    this._viewer.onDocumentChanged(() => this.refresh());
    // The noise layer takes no pointer events, so a box cannot report its own
    // hover. One delegated listener hit-tests instead, which keeps the layer
    // inert: a rectangle drag begun over a noise box still reaches the page.
    this._viewer.element.addEventListener("pointermove", this._onPointerMove);
    this._viewer.element.addEventListener("pointerleave", this._hideTip);
  }

  onValueClicked(callback: (pdfId: string, pageIndex: number, value: FinancialValue) => void): void {
    this._callbacks.push(callback);
  }

  toggle(): boolean { this._debugVisible ? this.hide() : this.show(); return this._debugVisible; }

  /** Show every hit box. Click targets themselves are always active. */
  show(): void { this._debugVisible = true; this._renderAll(); }

  /** Hide diagnostic boxes while retaining transparent clickable targets. */
  hide(): void { this._debugVisible = false; this._renderAll(); }

  toggleNoise(): boolean { this._noiseVisible ? this.hideNoise() : this.showNoise(); return this._noiseVisible; }

  /**
   * Show the spans the detector recognized and then refused, each labelled with
   * the rule that refused it. Diagnostics only: noise draws nothing clickable,
   * so it can be left on while linking values.
   */
  showNoise(): void { this._noiseVisible = true; this._renderAll(); }

  hideNoise(): void { this._noiseVisible = false; this._hideTip(); this._renderAll(); }

  refresh(): void { this._renderAll(); }

  private _renderAll(): void {
    const pdfId = this._viewer.getActivePdfId();
    if (!pdfId) return;
    for (const { pageNumber, wrapper } of this._viewer.getPageLayout()) {
      this._renderPage(wrapper, pdfId, pageNumber - 1);
    }
  }

  private _renderPage(wrapper: HTMLDivElement, pdfId: string, pageIndex: number): void {
    this._clearPage(wrapper);
    const values = this._cache.valuesOnPage(pdfId, pageIndex);
    const noise = this._noiseVisible ? this._cache.noiseOnPage(pdfId, pageIndex) : [];
    if (values.length === 0 && noise.length === 0) return;
    const layer = ensureOverlayLayer(wrapper);
    if (values.length > 0) {
      const container = document.createElement("div");
      container.className = this._debugVisible ? `${OVERLAY_CLASS} ${OVERLAY_CLASS}--debug` : OVERLAY_CLASS;
      for (const value of values) container.appendChild(this._valueElement(pdfId, pageIndex, value));
      layer.appendChild(container);
    }
    if (noise.length > 0) {
      const container = document.createElement("div");
      container.className = NOISE_CLASS;
      for (const entry of noise) container.appendChild(FsValuesOverlay._noiseElement(entry));
      layer.appendChild(container);
    }
  }

  /** An inert marker. Noise is never focusable and never takes a pointer event. */
  private static _noiseElement(entry: FsNoiseValue): HTMLDivElement {
    const element = document.createElement("div");
    element.className = `${NOISE_CLASS}__value ${NOISE_CLASS}__value--${entry.kind}`;
    element.style.left = `${entry.bounds.x * 100}%`;
    element.style.top = `${entry.bounds.y * 100}%`;
    element.style.width = `${entry.bounds.width * 100}%`;
    element.style.height = `${entry.bounds.height * 100}%`;
    element.dataset["fsNoiseReason"] = entry.reason;
    element.setAttribute("aria-hidden", "true");
    return element;
  }

  private _valueElement(pdfId: string, pageIndex: number, value: FinancialValue): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `fs-values__value fs-values__value--${value.kind}`;
    button.style.left = `${value.bounds.x * 100}%`;
    button.style.top = `${value.bounds.y * 100}%`;
    button.style.width = `${value.bounds.width * 100}%`;
    button.style.height = `${value.bounds.height * 100}%`;
    button.title = `Create link for ${value.kind}: ${value.text}`;
    button.dataset["fsValueId"] = value.id;
    button.setAttribute("aria-label", `Create link for detected ${value.kind} ${value.text}`);
    button.addEventListener("pointerdown", (event) => event.stopPropagation());
    button.addEventListener("mousedown", (event) => {
      event.preventDefault();
      event.stopPropagation();
    });
    const setRelatedHover = (active: boolean): void => {
      for (const related of Array.from(
        this._viewer.element.querySelectorAll<HTMLElement>(".fs-values__value"),
      ).filter((candidate) => candidate.dataset["fsValueId"] === value.id)) {
        related.classList.toggle("fs-values__value--related-hover", active);
      }
    };
    button.addEventListener("pointerenter", () => setRelatedHover(true));
    button.addEventListener("pointerleave", () => setRelatedHover(false));
    button.addEventListener("focus", () => setRelatedHover(true));
    button.addEventListener("blur", () => setRelatedHover(false));
    button.addEventListener("click", (event) => {
      event.preventDefault(); event.stopPropagation();
      if (button.disabled) return;
      button.disabled = true;
      globalThis.setTimeout(() => { button.disabled = false; }, 1_500);
      for (const callback of this._callbacks) callback(pdfId, pageIndex, value);
    });
    return button;
  }

  /** Why each suppressor refused a span, in the reader's words. */
  private static readonly _EXPLANATIONS: Record<FsNoiseValue["reason"], string> = {
    "joined-token": "Part of a hyphen, slash or colon joined identifier",
    "page-furniture": "On a running header or footer, repeated across pages",
    "phone-context": "Inside a phone number",
    "identifier-context": "Follows a label that introduces a reference number",
    "superscript": "Set smaller than the page's text, so a footnote marker",
    "citation-year": "A year reached through a citation, so not a period",
  };

  private readonly _onPointerMove = (event: PointerEvent): void => {
    if (!this._noiseVisible) return;
    const pdfId = this._viewer.getActivePdfId();
    if (!pdfId) { this._hideTip(); return; }
    for (const { pageNumber, wrapper } of this._viewer.getPageLayout()) {
      const rect = wrapper.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) continue;
      if (event.clientX < rect.left || event.clientX > rect.right) continue;
      if (event.clientY < rect.top || event.clientY > rect.bottom) continue;
      const x = (event.clientX - rect.left) / rect.width;
      const y = (event.clientY - rect.top) / rect.height;
      // Smallest box wins, so a marker inside a wider one stays reachable.
      let hit: FsNoiseValue | undefined;
      let hitArea = Infinity;
      for (const entry of this._cache.noiseOnPage(pdfId, pageNumber - 1)) {
        const { x: left, y: top, width, height } = entry.bounds;
        if (x < left || x > left + width || y < top || y > top + height) continue;
        const area = width * height;
        if (area < hitArea) { hit = entry; hitArea = area; }
      }
      if (hit) this._showTip(hit, event.clientX, event.clientY);
      else this._hideTip();
      return;
    }
    this._hideTip();
  };

  private _showTip(entry: FsNoiseValue, clientX: number, clientY: number): void {
    const tip = this._tip ?? this._createTip();
    if (this._tipFor !== entry.id) {
      this._tipFor = entry.id;
      tip.replaceChildren();
      const reason = document.createElement("span");
      reason.className = `${NOISE_CLASS}__tip-reason`;
      reason.textContent = entry.reason;
      const why = document.createElement("span");
      why.className = `${NOISE_CLASS}__tip-why`;
      why.textContent = FsValuesOverlay._EXPLANATIONS[entry.reason];
      const text = document.createElement("span");
      text.className = `${NOISE_CLASS}__tip-text`;
      text.textContent = entry.text;
      tip.append(reason, why, text);
      tip.dataset["fsNoiseReason"] = entry.reason;
      tip.hidden = false;
      // Measured once per entry. Reading it on every move would force a layout
      // between the style writes below, on an event that fires continuously.
      this._tipWidth = tip.offsetWidth;
      this._tipHeight = tip.offsetHeight;
    }
    tip.hidden = false;
    // Flipped near the right or bottom edge so the tip stays on screen.
    const width = this._tipWidth;
    const height = this._tipHeight;
    const left = clientX + 14 + width > window.innerWidth ? clientX - 14 - width : clientX + 14;
    const top = clientY + 14 + height > window.innerHeight ? clientY - 14 - height : clientY + 14;
    tip.style.left = `${Math.max(4, left)}px`;
    tip.style.top = `${Math.max(4, top)}px`;
  }

  private _createTip(): HTMLDivElement {
    const tip = document.createElement("div");
    tip.className = `${NOISE_CLASS}__tip`;
    tip.hidden = true;
    document.body.appendChild(tip);
    this._tip = tip;
    return tip;
  }

  private readonly _hideTip = (): void => {
    if (this._tip) this._tip.hidden = true;
    this._tipFor = "";
  };

  private _clearAll(): void {
    for (const { wrapper } of this._viewer.getPageLayout()) this._clearPage(wrapper);
  }

  private _clearPage(wrapper: HTMLDivElement): void {
    for (const element of Array.from(
      wrapper.querySelectorAll(`.${OVERLAY_CLASS}, .${NOISE_CLASS}`),
    )) element.remove();
  }
}
