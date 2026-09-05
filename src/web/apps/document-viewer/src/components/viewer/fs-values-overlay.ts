import type {
  FinancialValue,
  FsNoiseValue,
  FsNoteHeader,
  FsValueBounds,
  HoverTipContent,
} from "@doculink/shared";
import {
  HoverTip,
  describeFsNoise,
  describeFsNoteHeader,
  describeFsValue,
} from "@doculink/shared";
import type { FsValuesCache } from "../../services/fs-values-cache.js";
import type { PdfViewer } from "./pdf-viewer.js";
import { ensureOverlayLayer } from "./page-renderer.js";

const OVERLAY_CLASS = "fs-values";
const NOISE_CLASS = "fs-values-noise";

function sameBounds(left: FsValueBounds, right: FsValueBounds): boolean {
  return left.x === right.x
    && left.y === right.y
    && left.width === right.width
    && left.height === right.height;
}

export class FsValuesOverlay {
  private _debugVisible = false;
  private _noiseVisible = false;
  private readonly _tip: HoverTip;
  private readonly _callbacks: Array<(pdfId: string, pageIndex: number, value: FinancialValue) => void> = [];
  private readonly _viewer: PdfViewer;
  private readonly _cache: FsValuesCache;

  constructor(viewer: PdfViewer, cache: FsValuesCache) {
    this._viewer = viewer;
    this._cache = cache;
    this._viewer.onDocumentChanged(() => this.refresh());
    // Neither layer can report its own hover: noise takes no pointer events at
    // all, and a value box is a click target whose own tooltip would fight this
    // one. The tip hit-tests the cursor instead, and only while a debug view
    // asks for it.
    this._tip = new HoverTip({
      className: "fs-values__tip",
      resolve: (clientX, clientY) => this._describeAt(clientX, clientY),
    });
  }

  onValueClicked(callback: (pdfId: string, pageIndex: number, value: FinancialValue) => void): void {
    this._callbacks.push(callback);
  }

  toggle(): boolean { this._debugVisible ? this.hide() : this.show(); return this._debugVisible; }

  /** Show every hit box. Click targets themselves are always active. */
  show(): void { this._debugVisible = true; this._syncTip(); this._renderAll(); }

  /** Hide diagnostic boxes while retaining transparent clickable targets. */
  hide(): void { this._debugVisible = false; this._syncTip(); this._renderAll(); }

  toggleNoise(): boolean { this._noiseVisible ? this.hideNoise() : this.showNoise(); return this._noiseVisible; }

  /**
   * Show spans classified as noise, each labelled with its group. This includes
   * refused values and complete note headings/references. Diagnostics only:
   * noise draws nothing clickable, so it can be left on while linking values.
   */
  showNoise(): void { this._noiseVisible = true; this._syncTip(); this._renderAll(); }

  hideNoise(): void { this._noiseVisible = false; this._syncTip(); this._renderAll(); }

  refresh(): void { this._renderAll(); }

  private _renderAll(): void {
    // Zoom, re-render and page changes all move boxes out from under a held
    // cursor without a pointer event of any kind.
    this._tip.refresh();
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
    // The debug view explains this box in the hover tip; two tooltips would
    // race each other, so the native one only stands in when it is off.
    if (!this._debugVisible) button.title = `Create link for ${value.kind}: ${value.text}`;
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

  /** The tip is a debug aid, so it opens only while a debug view is on. */
  private _syncTip(): void {
    this._tip.setEnabled(this._debugVisible || this._noiseVisible);
  }

  /**
   * What sits under a client point: the smallest box across whichever layers
   * are showing, so a footnote marker inside a wider span stays reachable.
   */
  private _describeAt(clientX: number, clientY: number): HoverTipContent | null {
    const pdfId = this._viewer.getActivePdfId();
    if (!pdfId) return null;
    for (const { pageNumber, wrapper } of this._viewer.getPageLayout()) {
      const rect = wrapper.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) continue;
      if (clientX < rect.left || clientX > rect.right) continue;
      if (clientY < rect.top || clientY > rect.bottom) continue;
      const x = (clientX - rect.left) / rect.width;
      const y = (clientY - rect.top) / rect.height;
      const pageIndex = pageNumber - 1;
      let best: HoverTipContent | null = null;
      let bestArea = Infinity;
      const consider = (
        bounds: FinancialValue["bounds"],
        describe: () => HoverTipContent,
      ): void => {
        if (x < bounds.x || x > bounds.x + bounds.width) return;
        if (y < bounds.y || y > bounds.y + bounds.height) return;
        const area = bounds.width * bounds.height;
        if (area >= bestArea) return;
        bestArea = area;
        best = describe();
      };
      if (this._debugVisible) {
        for (const value of this._cache.valuesOnPage(pdfId, pageIndex)) {
          consider(value.bounds, () => describeFsValue(value));
        }
      }
      if (this._noiseVisible) {
        for (const entry of this._cache.noiseOnPage(pdfId, pageIndex)) {
          consider(entry.bounds, () => this._describeNoise(pdfId, pageIndex, entry));
        }
      }
      return best;
    }
    return null;
  }

  private _describeNoise(
    pdfId: string,
    pageIndex: number,
    entry: FsNoiseValue,
  ): HoverTipContent {
    if (entry.reason !== "note-header") return describeFsNoise(entry);
    for (const note of this._cache.notes(pdfId)) {
      const header = note.headers.find((candidate: FsNoteHeader) => (
        candidate.pageIndex === pageIndex
        && candidate.text === entry.text
        && sameBounds(candidate.bounds, entry.bounds)
      ));
      if (header) return describeFsNoteHeader(entry, note, header);
    }
    return describeFsNoise(entry);
  }

  private _clearAll(): void {
    for (const { wrapper } of this._viewer.getPageLayout()) this._clearPage(wrapper);
  }

  private _clearPage(wrapper: HTMLDivElement): void {
    for (const element of Array.from(
      wrapper.querySelectorAll(`.${OVERLAY_CLASS}, .${NOISE_CLASS}`),
    )) element.remove();
  }
}
