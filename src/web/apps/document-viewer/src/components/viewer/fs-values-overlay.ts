import type { FinancialValue } from "@doculink/shared";
import type { FsValuesCache } from "../../services/fs-values-cache.js";
import type { PdfViewer } from "./pdf-viewer.js";
import { ensureOverlayLayer } from "./page-renderer.js";

const OVERLAY_CLASS = "fs-values";

export class FsValuesOverlay {
  private _debugVisible = false;
  private readonly _callbacks: Array<(pdfId: string, pageIndex: number, value: FinancialValue) => void> = [];
  private readonly _viewer: PdfViewer;
  private readonly _cache: FsValuesCache;

  constructor(viewer: PdfViewer, cache: FsValuesCache) {
    this._viewer = viewer;
    this._cache = cache;
    this._viewer.onDocumentChanged(() => this.refresh());
  }

  onValueClicked(callback: (pdfId: string, pageIndex: number, value: FinancialValue) => void): void {
    this._callbacks.push(callback);
  }

  toggle(): boolean { this._debugVisible ? this.hide() : this.show(); return this._debugVisible; }

  /** Show every hit box. Click targets themselves are always active. */
  show(): void { this._debugVisible = true; this._renderAll(); }

  /** Hide diagnostic boxes while retaining transparent clickable targets. */
  hide(): void { this._debugVisible = false; this._renderAll(); }

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
    if (values.length === 0) return;
    const container = document.createElement("div");
    container.className = this._debugVisible ? `${OVERLAY_CLASS} ${OVERLAY_CLASS}--debug` : OVERLAY_CLASS;
    for (const value of values) container.appendChild(this._valueElement(pdfId, pageIndex, value));
    ensureOverlayLayer(wrapper).appendChild(container);
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
    button.addEventListener("click", (event) => {
      event.preventDefault(); event.stopPropagation();
      if (button.disabled) return;
      button.disabled = true;
      globalThis.setTimeout(() => { button.disabled = false; }, 1_500);
      for (const callback of this._callbacks) callback(pdfId, pageIndex, value);
    });
    return button;
  }

  private _clearAll(): void {
    for (const { wrapper } of this._viewer.getPageLayout()) this._clearPage(wrapper);
  }

  private _clearPage(wrapper: HTMLDivElement): void {
    for (const element of Array.from(wrapper.querySelectorAll(`.${OVERLAY_CLASS}`))) element.remove();
  }
}
