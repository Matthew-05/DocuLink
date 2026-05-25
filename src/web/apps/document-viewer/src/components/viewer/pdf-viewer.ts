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
    return Array.from(
      this.element.querySelectorAll<HTMLDivElement>("div[data-page]")
    ).map((wrapper, i) => ({ pageNumber: i + 1, wrapper }));
  }

  async loadDocument(url: string, pdfId?: string, priorityPage = 1): Promise<void> {
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

    const wrappers = this._ensurePageWrappers(this._doc.numPages);

    // Render the target page first so navigation can complete immediately.
    const target = Math.max(1, Math.min(priorityPage, this._doc.numPages));
    const targetWrapper = wrappers[target - 1]!;
    const targetDims = await renderPage(this._doc, target, targetWrapper, this._scale);
    if (generation !== this._loadGeneration) return;
    this._pageEntries[target - 1] = { wrapper: targetWrapper, ...targetDims };

    for (const cb of this._onDocumentChangedCallbacks) {
      cb();
    }

    // Render remaining pages in the background.
    const capturedDoc = this._doc;
    this._renderingQueue = this._renderingQueue.then(async () => {
      for (let i = 1; i <= capturedDoc.numPages; i++) {
        if (i === target) continue;
        if (generation !== this._loadGeneration) return;
        const wrapper = wrappers[i - 1];
        if (!wrapper) continue;
        const dims = await renderPage(capturedDoc, i, wrapper, this._scale);
        if (generation !== this._loadGeneration) return;
        this._pageEntries[i - 1] = { wrapper, ...dims };
      }
    });
  }

  setZoom(scale: ZoomLevel, anchor?: { x: number; y: number }): void {
    const oldScale = this._scale;
    if (oldScale === scale) return;

    // Instantly resize wrapper divs so the layout reflows correctly — pages
    // respace immediately without any CSS transform hackery. The canvas inside
    // each wrapper fills it via CSS (width/height: 100%), so it stretches to
    // preview the new size until the debounced re-render fires.
    for (const entry of this._pageEntries) {
      if (!entry) continue;
      entry.wrapper.style.width = `${entry.baseWidth * scale}px`;
      entry.wrapper.style.height = `${entry.baseHeight * scale}px`;
    }

    const ax = anchor?.x ?? this.element.clientWidth / 2;
    const ay = anchor?.y ?? this.element.clientHeight / 2;
    const ratio = scale / oldScale;

    this.element.scrollLeft = (this.element.scrollLeft + ax) * ratio - ax;
    this.element.scrollTop = (this.element.scrollTop + ay) * ratio - ay;

    this.element.scrollLeft = Math.max(
      0,
      Math.min(this.element.scrollLeft, this.element.scrollWidth - this.element.clientWidth)
    );
    this.element.scrollTop = Math.max(
      0,
      Math.min(this.element.scrollTop, this.element.scrollHeight - this.element.clientHeight)
    );

    this._scale = scale;

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

  /** Returns the scale that fits the given page fully within the viewer viewport, or null if page dims are not yet known. */
  getPageFitScale(pageNumber: number): ZoomLevel | null {
    const entry = this._pageEntries[pageNumber - 1];
    if (!entry) return null;
    const w = this.element.clientWidth;
    const h = this.element.clientHeight;
    if (!w || !h) return null;
    return Math.min(w / entry.baseWidth, h / entry.baseHeight);
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
