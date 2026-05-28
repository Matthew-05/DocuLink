import type * as pdfjsLib from "pdfjs-dist";
import type { ZoomLevel } from "../../types/index.js";
import { loadPdfDocument } from "./pdf-loader.js";
import { renderPage } from "./page-renderer.js";

const ZOOM_DEBOUNCE_MS = 300;

interface PageEntry {
  wrapper: HTMLDivElement;
  baseWidth: number;
  baseHeight: number;
  renderedScale: ZoomLevel | null;
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
  private _renderGeneration = 0;
  private _loadComplete: Promise<void> = Promise.resolve();

  private readonly _onLoadedCallbacks: Array<(totalPages: number) => void> = [];
  private readonly _onDocumentChangedCallbacks: Array<() => void> = [];

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "viewer";

    const placeholder = document.createElement("div");
    placeholder.className = "viewer__placeholder";
    placeholder.textContent = "DocuLink Initializing…";
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

  getCurrentZoom(): ZoomLevel {
    return this._scale;
  }

  waitForLoad(): Promise<void> {
    return this._loadComplete;
  }

  showNoPdfsState(): void {
    this.element.replaceChildren();
    const placeholder = document.createElement("div");
    placeholder.className = "viewer__placeholder";
    placeholder.textContent = "No PDFs added";
    this.element.appendChild(placeholder);
  }

  getPageLayout(): Array<{ pageNumber: number; wrapper: HTMLDivElement }> {
    return Array.from(
      this.element.querySelectorAll<HTMLDivElement>("div[data-page]")
    ).map((wrapper, i) => ({ pageNumber: i + 1, wrapper }));
  }

  async loadDocument(url: string, pdfId?: string): Promise<void> {
    const generation = ++this._loadGeneration;
    this._cancelZoomDebounce();
    this._activePdfId = pdfId ?? null;
    this._pageEntries = [];
    this.element.replaceChildren();

    let resolveLoad!: () => void;
    this._loadComplete = new Promise<void>((resolve) => { resolveLoad = resolve; });

    const doc = await loadPdfDocument(url, this._doc);
    if (generation !== this._loadGeneration) {
      doc.destroy();
      resolveLoad();
      return;
    }

    this._doc = doc;

    // Fetch all page dimensions in parallel — no canvas, just metadata.
    const pages = await Promise.all(
      Array.from({ length: doc.numPages }, (_, i) => doc.getPage(i + 1))
    );
    if (generation !== this._loadGeneration) {
      resolveLoad();
      return;
    }

    const dims = pages.map(page => {
      const vp = page.getViewport({ scale: 1.0 });
      page.cleanup();
      return { baseWidth: vp.width, baseHeight: vp.height };
    });

    // Create all wrappers with correct dimensions and populate _pageEntries fully.
    // renderedScale is initialized to null — all pages are unrendered.
    this._createPageWrappers(doc.numPages, dims);

    for (const cb of this._onLoadedCallbacks) {
      cb(doc.numPages);
    }

    for (const cb of this._onDocumentChangedCallbacks) {
      cb();
    }

    // Signal that document load and page wrappers are ready.
    // No canvas renders have happened — all pages are white placeholders.
    // Callers are responsible for calling startBackgroundRender() or renderPageNow().
    resolveLoad();
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

  getPageFitScale(pageNumber: number): ZoomLevel | null {
    const entry = this._pageEntries[pageNumber - 1];
    if (!entry) return null;
    const w = this.element.clientWidth;
    const h = this.element.clientHeight;
    if (!w || !h) return null;
    return Math.min(w / entry.baseWidth, h / entry.baseHeight);
  }

  startBackgroundRender(): void {
    const renderGen = ++this._renderGeneration;
    const doc = this._doc;
    if (!doc) return;
    const generation = this._loadGeneration;
    const capturedScale = this._scale;

    this._renderingQueue = this._renderingQueue.then(async () => {
      for (let i = 1; i <= doc.numPages; i++) {
        if (renderGen !== this._renderGeneration) return;
        if (generation !== this._loadGeneration) return;
        const entry = this._pageEntries[i - 1];
        if (!entry) continue;
        if (entry.renderedScale === capturedScale) continue;
        await renderPage(doc, i, entry.wrapper, capturedScale);
        entry.renderedScale = capturedScale;
      }
    });
  }

  async renderPageNow(pageNumber: number): Promise<void> {
    ++this._renderGeneration;
    const doc = this._doc;
    if (!doc) return;

    await new Promise<void>((resolve) => {
      this._renderingQueue = this._renderingQueue.then(async () => {
        const entry = this._pageEntries[pageNumber - 1];
        if (!entry || this._doc !== doc) {
          resolve();
          return;
        }
        if (entry.renderedScale === this._scale) {
          resolve();
          return;
        }
        await renderPage(doc, pageNumber, entry.wrapper, this._scale);
        entry.renderedScale = this._scale;
        resolve();
      });
    });
  }

  private async _renderAll(): Promise<void> {
    const doc = this._doc;
    if (!doc) return;

    for (let i = 0; i < this._pageEntries.length; i++) {
      const entry = this._pageEntries[i];
      if (!entry || this._doc !== doc) continue;
      if (entry.renderedScale === this._scale) continue;
      await renderPage(doc, i + 1, entry.wrapper, this._scale);
      entry.renderedScale = this._scale;
    }
  }

  private _createPageWrappers(
    total: number,
    dims: Array<{ baseWidth: number; baseHeight: number }>,
  ): void {
    this.element.replaceChildren();

    for (let i = 0; i < total; i++) {
      const div = document.createElement("div");
      div.className = "viewer__page";
      div.dataset["page"] = String(i + 1);
      div.style.width = `${dims[i]!.baseWidth * this._scale}px`;
      div.style.height = `${dims[i]!.baseHeight * this._scale}px`;
      this.element.appendChild(div);
      this._pageEntries[i] = { wrapper: div, renderedScale: null, ...dims[i]! };
    }
  }

  private _cancelZoomDebounce(): void {
    if (this._zoomDebounce !== null) {
      clearTimeout(this._zoomDebounce);
      this._zoomDebounce = null;
    }
  }
}
