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

    // Fetch all page dimensions in parallel — no canvas, just metadata.
    const pages = await Promise.all(
      Array.from({ length: doc.numPages }, (_, i) => doc.getPage(i + 1))
    );
    if (generation !== this._loadGeneration) return;

    const dims = pages.map(page => {
      const vp = page.getViewport({ scale: 1.0 });
      page.cleanup();
      return { baseWidth: vp.width, baseHeight: vp.height };
    });

    // Create all wrappers with correct dimensions and populate _pageEntries fully.
    const wrappers = this._createPageWrappers(doc.numPages, dims);

    for (const cb of this._onLoadedCallbacks) {
      cb(doc.numPages);
    }

    // Render the priority page canvas so the user sees something immediately.
    const target = Math.max(1, Math.min(priorityPage, doc.numPages));
    console.log(`[PdfViewer] Priority render: page ${target} at scale ${this._scale}`);
    await renderPage(doc, target, wrappers[target - 1]!, this._scale);
    if (generation !== this._loadGeneration) return;

    for (const cb of this._onDocumentChangedCallbacks) {
      cb();
    }

    // Render remaining pages in the background.
    const capturedDoc = doc;
    this._renderingQueue = this._renderingQueue.then(async () => {
      console.log(`[PdfViewer] Background rendering remaining pages`);
      for (let i = 1; i <= capturedDoc.numPages; i++) {
        if (i === target) continue;
        if (generation !== this._loadGeneration) return;
        const entry = this._pageEntries[i - 1];
        if (!entry) continue;
        await renderPage(capturedDoc, i, entry.wrapper, this._scale);
        console.log(`[PdfViewer] Background rendered page ${i}`);
      }
      console.log(`[PdfViewer] Background rendering complete`);
    });
  }

  setZoom(scale: ZoomLevel, anchor?: { x: number; y: number }): void {
    const oldScale = this._scale;
    if (oldScale === scale) return;

    console.log(`[PdfViewer] setZoom: ${oldScale} → ${scale}`);

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
      console.log(`[PdfViewer] Debounce timeout: starting _renderAll at scale ${this._scale}`);
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
    const fitScale = Math.min(w / entry.baseWidth, h / entry.baseHeight);
    console.log(`[PdfViewer] getPageFitScale(${pageNumber}): base=${entry.baseWidth}×${entry.baseHeight}, viewport=${w}×${h}, scale=${fitScale}`);
    return fitScale;
  }

  async renderPageNow(pageNumber: number): Promise<void> {
    const doc = this._doc;
    if (!doc) return;
    const entry = this._pageEntries[pageNumber - 1];
    if (!entry) return;
    if (entry.wrapper.querySelector("canvas")) return;
    await renderPage(doc, pageNumber, entry.wrapper, this._scale);
  }

  private async _renderAll(): Promise<void> {
    const doc = this._doc;
    if (!doc) return;

    console.log(`[PdfViewer] _renderAll at scale ${this._scale}`);
    for (let i = 0; i < this._pageEntries.length; i++) {
      const entry = this._pageEntries[i];
      if (!entry || this._doc !== doc) continue;
      await renderPage(doc, i + 1, entry.wrapper, this._scale);
    }
  }

  private _createPageWrappers(
    total: number,
    dims: Array<{ baseWidth: number; baseHeight: number }>,
  ): HTMLDivElement[] {
    this.element.replaceChildren();
    const result: HTMLDivElement[] = [];

    for (let i = 0; i < total; i++) {
      const div = document.createElement("div");
      div.className = "viewer__page";
      div.dataset["page"] = String(i + 1);
      div.style.width = `${dims[i]!.baseWidth * this._scale}px`;
      div.style.height = `${dims[i]!.baseHeight * this._scale}px`;
      this.element.appendChild(div);
      this._pageEntries[i] = { wrapper: div, ...dims[i]! };
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
