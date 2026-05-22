import type * as pdfjsLib from "pdfjs-dist";
import type { ZoomLevel } from "../../types/index.js";
import { loadPdfDocument } from "./pdf-loader.js";
import { renderPage } from "./page-renderer.js";

const ZOOM_DEBOUNCE_MS = 300;

interface PageEntry {
  wrapper: HTMLDivElement;
  baseWidth: number;
  baseHeight: number;
}

export class PdfViewer {
  readonly element: HTMLElement;

  private _doc: pdfjsLib.PDFDocumentProxy | null = null;
  private _activePdfId: string | null = null;
  private _scale: ZoomLevel = 1.0;
  private _pageEntries: PageEntry[] = [];
  private _renderingQueue: Promise<void> = Promise.resolve();
  private _zoomDebounce: ReturnType<typeof setTimeout> | null = null;
  private _loadGeneration = 0;

  private readonly _onLoadedCallbacks: Array<(totalPages: number) => void> = [];
  private readonly _onDocumentChangedCallbacks: Array<() => void> = [];

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "viewer";

    const placeholder = document.createElement("div");
    placeholder.className = "viewer__placeholder";
    placeholder.textContent = "No document selected";
    this.element.appendChild(placeholder);
  }

  onLoaded(cb: (totalPages: number) => void): void {
    this._onLoadedCallbacks.push(cb);
  }

  onDocumentChanged(cb: () => void): void {
    this._onDocumentChangedCallbacks.push(cb);
  }

  getDocument(): pdfjsLib.PDFDocumentProxy | null {
    return this._doc;
  }

  getActivePdfId(): string | null {
    return this._activePdfId;
  }

  getPageLayout(): Array<{ pageNumber: number; wrapper: HTMLDivElement }> {
    const result: Array<{ pageNumber: number; wrapper: HTMLDivElement }> = [];
    for (let i = 0; i < this._pageEntries.length; i++) {
      const entry = this._pageEntries[i];
      if (entry) {
        result.push({ pageNumber: i + 1, wrapper: entry.wrapper });
      }
    }
    return result;
  }

  async loadDocument(url: string, pdfId?: string): Promise<void> {
    const generation = ++this._loadGeneration;
    this._cancelZoomDebounce();
    this._activePdfId = pdfId ?? null;
    this._pageEntries = [];
    this.element.replaceChildren();

    const doc = await loadPdfDocument(url, this._doc);
    if (generation !== this._loadGeneration) {
      doc.destroy();
      return;
    }

    this._doc = doc;

    for (const cb of this._onLoadedCallbacks) {
      cb(this._doc.numPages);
    }

    await this._renderAll();
    if (generation !== this._loadGeneration) return;

    for (const cb of this._onDocumentChangedCallbacks) {
      cb();
    }
  }

  setZoom(scale: ZoomLevel): void {
    this._scale = scale;

    // Instantly resize wrapper divs so the layout reflows correctly — pages
    // respace immediately without any CSS transform hackery. The canvas inside
    // each wrapper fills it via CSS (width/height: 100%), so it stretches to
    // preview the new size until the debounced re-render fires.
    for (const { wrapper, baseWidth, baseHeight } of this._pageEntries) {
      wrapper.style.width = `${baseWidth * scale}px`;
      wrapper.style.height = `${baseHeight * scale}px`;
    }

    this._cancelZoomDebounce();
    this._zoomDebounce = setTimeout(() => {
      this._zoomDebounce = null;
      this._renderingQueue = this._renderingQueue.then(() => this._renderAll());
    }, ZOOM_DEBOUNCE_MS);
  }

  scrollToPage(pageNumber: number): void {
    const wrapper = this.element.querySelector<HTMLDivElement>(
      `[data-page="${pageNumber}"]`
    );
    wrapper?.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  private async _renderAll(): Promise<void> {
    if (!this._doc) return;

    const wrappers = this._ensurePageWrappers(this._doc.numPages);

    for (let i = 1; i <= this._doc.numPages; i++) {
      const wrapper = wrappers[i - 1];
      if (!wrapper) continue;
      const { baseWidth, baseHeight } = await renderPage(this._doc, i, wrapper, this._scale);
      this._pageEntries[i - 1] = { wrapper, baseWidth, baseHeight };
    }
  }

  private _ensurePageWrappers(total: number): HTMLDivElement[] {
    const existing = Array.from(
      this.element.querySelectorAll<HTMLDivElement>("div[data-page]")
    );

    if (existing.length === total) return existing;

    this.element.replaceChildren();
    const result: HTMLDivElement[] = [];

    for (let i = 1; i <= total; i++) {
      const div = document.createElement("div");
      div.className = "viewer__page";
      div.dataset["page"] = String(i);
      this.element.appendChild(div);
      result.push(div);
    }

    return result;
  }

  private _cancelZoomDebounce(): void {
    if (this._zoomDebounce !== null) {
      clearTimeout(this._zoomDebounce);
      this._zoomDebounce = null;
    }
  }
}
