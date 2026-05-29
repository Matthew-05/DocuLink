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
  rotation: number;
}

export class PdfViewer {
  readonly element: HTMLElement;

  private _doc: pdfjsLib.PDFDocumentProxy | null = null;
  private _activePdfId: string | null = null;
  private _scale: ZoomLevel = 1.0;
  private _pageEntries: PageEntry[] = [];
  private _pageRotations = new Map<number, number>();
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

  async loadDocument(
    url: string,
    pdfId?: string,
    pageRotations?: Record<number, number>,
  ): Promise<void> {
    const generation = ++this._loadGeneration;
    this._cancelZoomDebounce();
    this._activePdfId = pdfId ?? null;
    this._pageEntries = [];
    this._pageRotations.clear();
    if (pageRotations) {
      for (const [k, v] of Object.entries(pageRotations)) {
        const n = Number(k);
        if (v !== 0) this._pageRotations.set(n, v);
      }
    }
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

    const dims = pages.map((page, i) => {
      const rotation = this._pageRotations.get(i) ?? 0;
      const vp = page.getViewport({ scale: 1.0, rotation });
      page.cleanup();
      return { baseWidth: vp.width, baseHeight: vp.height, rotation };
    });

    // Create all wrappers with correct dimensions and populate _pageEntries fully.
    // renderedScale is initialized to null — all pages are unrendered.
    this._createPageWrappers(doc.numPages, dims as Array<{ baseWidth: number; baseHeight: number; rotation: number }>);

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
        await renderPage(
          doc,
          i,
          entry.wrapper,
          capturedScale,
          entry.rotation,
          () => renderGen === this._renderGeneration && generation === this._loadGeneration,
        );
        if (renderGen !== this._renderGeneration || generation !== this._loadGeneration) return;
        entry.renderedScale = capturedScale;
      }
    });
  }

  async renderPageNow(pageNumber: number): Promise<void> {
    const doc = this._doc;
    if (!doc) return;
    const renderGen = ++this._renderGeneration;

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
        await renderPage(
          doc,
          pageNumber,
          entry.wrapper,
          this._scale,
          entry.rotation,
          () => renderGen === this._renderGeneration && this._doc === doc,
        );
        if (renderGen !== this._renderGeneration || this._doc !== doc) {
          resolve();
          return;
        }
        entry.renderedScale = this._scale;
        resolve();
      });
    });
  }

  private async _renderAll(): Promise<void> {
    const doc = this._doc;
    if (!doc) return;
    const renderGen = this._renderGeneration;

    for (let i = 0; i < this._pageEntries.length; i++) {
      const entry = this._pageEntries[i];
      if (renderGen !== this._renderGeneration) return;
      if (!entry || this._doc !== doc) continue;
      if (entry.renderedScale === this._scale) continue;
      await renderPage(
        doc,
        i + 1,
        entry.wrapper,
        this._scale,
        entry.rotation,
        () => renderGen === this._renderGeneration && this._doc === doc,
      );
      if (renderGen !== this._renderGeneration || this._doc !== doc) return;
      entry.renderedScale = this._scale;
    }
  }

  /** Updates the stored rotation for one page and triggers a re-render. */
  setPageRotation(pageIndex: number, rotation: number): void {
    this._pageRotations.set(pageIndex, rotation);
    const entry = this._pageEntries[pageIndex];
    const doc = this._doc;
    if (!entry || !doc) return;

    ++this._renderGeneration;
    const capturedRenderGen = this._renderGeneration;
    const previousRotation = entry.rotation;
    const previousWidth = entry.baseWidth;
    const previousHeight = entry.baseHeight;
    const rotationDelta = normalizeRotation(rotation - previousRotation);
    const previewWidth = rotationDelta === 90 || rotationDelta === 270 ? previousHeight : previousWidth;
    const previewHeight = rotationDelta === 90 || rotationDelta === 270 ? previousWidth : previousHeight;

    this._applyCssRotationPreview(entry, rotationDelta, previousWidth, previousHeight);
    entry.rotation = rotation;
    entry.baseWidth = previewWidth;
    entry.baseHeight = previewHeight;
    entry.renderedScale = null;
    entry.wrapper.style.width = `${previewWidth * this._scale}px`;
    entry.wrapper.style.height = `${previewHeight * this._scale}px`;

    const loadGen = this._loadGeneration;
    void doc.getPage(pageIndex + 1).then((page) => {
      if (this._loadGeneration !== loadGen) { page.cleanup(); return; }
      const vp = page.getViewport({ scale: 1.0, rotation });
      page.cleanup();
      if (this._renderGeneration !== capturedRenderGen) return;

      entry.baseWidth = vp.width;
      entry.baseHeight = vp.height;
      entry.wrapper.style.width = `${vp.width * this._scale}px`;
      entry.wrapper.style.height = `${vp.height * this._scale}px`;

      this._renderingQueue = this._renderingQueue.then(async () => {
        if (this._renderGeneration !== capturedRenderGen) return;
        const dims = await renderPage(
          doc,
          pageIndex + 1,
          entry.wrapper,
          this._scale,
          rotation,
          () => this._renderGeneration === capturedRenderGen && this._doc === doc,
        );
        if (this._renderGeneration !== capturedRenderGen) return;
        entry.baseWidth     = dims.baseWidth;
        entry.baseHeight    = dims.baseHeight;
        entry.renderedScale = this._scale;
      });
    });
  }

  private _createPageWrappers(
    total: number,
    dims: Array<{ baseWidth: number; baseHeight: number; rotation: number }>,
  ): void {
    this.element.replaceChildren();

    for (let i = 0; i < total; i++) {
      const { baseWidth, baseHeight, rotation } = dims[i]!;
      const div = document.createElement("div");
      div.className = "viewer__page";
      div.dataset["page"] = String(i + 1);
      div.style.width  = `${baseWidth  * this._scale}px`;
      div.style.height = `${baseHeight * this._scale}px`;
      this.element.appendChild(div);
      this._pageEntries[i] = { wrapper: div, renderedScale: null, baseWidth, baseHeight, rotation };
    }
  }

  private _cancelZoomDebounce(): void {
    if (this._zoomDebounce !== null) {
      clearTimeout(this._zoomDebounce);
      this._zoomDebounce = null;
    }
  }

  private _applyCssRotationPreview(
    entry: PageEntry,
    rotationDelta: number,
    previousWidth: number,
    previousHeight: number,
  ): void {
    const canvas = entry.wrapper.querySelector<HTMLCanvasElement>("canvas");
    if (!canvas || rotationDelta === 0) return;

    const width = previousWidth * this._scale;
    const height = previousHeight * this._scale;
    canvas.classList.add("viewer__canvas--rotation-preview");
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;
    canvas.style.transform = `translate(-50%, -50%) rotate(${rotationDelta}deg)`;
  }
}

function normalizeRotation(rotation: number): number {
  return ((rotation % 360) + 360) % 360;
}
