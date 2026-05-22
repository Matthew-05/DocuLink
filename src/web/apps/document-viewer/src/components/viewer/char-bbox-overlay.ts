import type { PdfViewer } from "./pdf-viewer.js";
import type { TextContentCache, CharacterEntry } from "../../services/text-content-cache.js";

const OVERLAY_CLASS = "char-bbox-debug";
const CELL_CLASS    = "char-bbox-debug__cell";

/**
 * Debug overlay that draws per-character bounding boxes from the text cache.
 * Toggle via the console: `__docuLink.toggleCharBboxes()`.
 */
export class CharBboxOverlay {
  private _visible = false;

  constructor(
    private readonly _viewer: PdfViewer,
    private readonly _cache: TextContentCache,
  ) {
    this._viewer.onDocumentChanged(() => {
      if (this._visible) this._renderAll();
      else this._clearAll();
    });
  }

  toggle(): boolean {
    this._visible = !this._visible;
    if (this._visible) this._renderAll();
    else this._clearAll();
    console.log(`[DocuLink] Char bbox overlay: ${this._visible ? "ON" : "OFF"}`);
    return this._visible;
  }

  show(): void {
    if (this._visible) return;
    this._visible = true;
    this._renderAll();
    console.log("[DocuLink] Char bbox overlay: ON");
  }

  hide(): void {
    if (!this._visible) return;
    this._visible = false;
    this._clearAll();
    console.log("[DocuLink] Char bbox overlay: OFF");
  }

  isVisible(): boolean {
    return this._visible;
  }

  /** Re-render when the text cache finishes building. */
  refresh(): void {
    if (this._visible) this._renderAll();
  }

  private _renderAll(): void {
    const pdfId = this._viewer.getActivePdfId();
    if (!pdfId) return;

    for (const { pageNumber, wrapper } of this._viewer.getPageLayout()) {
      this._renderPage(wrapper, pageNumber - 1, pdfId);
    }
  }

  private _renderPage(
    wrapper: HTMLDivElement,
    pageIndex: number,
    pdfId: string,
  ): void {
    this._clearPage(wrapper);

    const entries = this._cache.get(pdfId, pageIndex);
    if (!entries?.length) return;

    const container = document.createElement("div");
    container.className = OVERLAY_CLASS;

    const fragment = document.createDocumentFragment();
    for (const entry of entries) {
      fragment.appendChild(this._createCell(entry));
    }
    container.appendChild(fragment);
    wrapper.appendChild(container);
  }

  private _createCell(entry: CharacterEntry): HTMLDivElement {
    const div = document.createElement("div");
    div.className = CELL_CLASS;

    const width  = entry.normRight  - entry.normLeft;
    const height = entry.normBottom - entry.normTop;

    div.style.left   = `${entry.normLeft * 100}%`;
    div.style.top    = `${entry.normTop  * 100}%`;
    div.style.width  = `${width  * 100}%`;
    div.style.height = `${height * 100}%`;

    const hue = (entry.itemIndex * 47) % 360;
    div.style.borderColor  = `hsla(${hue}, 70%, 45%, 0.85)`;
    div.style.background   = `hsla(${hue}, 70%, 50%, 0.08)`;
    div.title = JSON.stringify({
      char: entry.char,
      itemIndex: entry.itemIndex,
      normLeft: entry.normLeft,
      normTop: entry.normTop,
      normRight: entry.normRight,
      normBottom: entry.normBottom,
    });

    return div;
  }

  private _clearAll(): void {
    for (const { wrapper } of this._viewer.getPageLayout()) {
      this._clearPage(wrapper);
    }
  }

  private _clearPage(wrapper: HTMLDivElement): void {
    for (const el of Array.from(wrapper.querySelectorAll(`.${OVERLAY_CLASS}`))) {
      el.remove();
    }
  }
}
