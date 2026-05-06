import * as pdfjsLib from "pdfjs-dist";
import type { ZoomLevel } from "../../types/index.js";

pdfjsLib.GlobalWorkerOptions.workerSrc = "pdf.worker.min.mjs";

export class PdfViewer {
  readonly element: HTMLElement;

  private _doc: pdfjsLib.PDFDocumentProxy | null = null;
  private _scale: ZoomLevel = 1.0;
  private _renderingQueue: Promise<void> = Promise.resolve();

  /** Fired when a document finishes loading; detail contains total page count. */
  private readonly _onLoadedCallbacks: Array<(totalPages: number) => void> = [];

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

  async loadDocument(url: string): Promise<void> {
    if (this._doc) {
      this._doc.destroy();
      this._doc = null;
    }

    this.element.replaceChildren();

    const loadingTask = pdfjsLib.getDocument(url);
    this._doc = await loadingTask.promise;

    for (const cb of this._onLoadedCallbacks) {
      cb(this._doc.numPages);
    }

    await this._renderAll();
  }

  setZoom(scale: ZoomLevel): void {
    this._scale = scale;
    this._renderingQueue = this._renderingQueue.then(() => this._renderAll());
  }

  scrollToPage(pageNumber: number): void {
    const canvas = this.element.querySelector<HTMLCanvasElement>(
      `[data-page="${pageNumber}"]`
    );
    canvas?.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  private async _renderAll(): Promise<void> {
    if (!this._doc) return;

    const total = this._doc.numPages;
    const canvases = this._ensureCanvases(total);

    for (let i = 1; i <= total; i++) {
      const canvas = canvases[i - 1];
      if (!canvas) continue;
      await this._renderPage(i, canvas);
    }
  }

  private _ensureCanvases(total: number): HTMLCanvasElement[] {
    const existing = Array.from(
      this.element.querySelectorAll<HTMLCanvasElement>("canvas[data-page]")
    );

    if (existing.length === total) return existing;

    this.element.replaceChildren();
    const result: HTMLCanvasElement[] = [];

    for (let i = 1; i <= total; i++) {
      const canvas = document.createElement("canvas");
      canvas.className = "viewer__page";
      canvas.dataset["page"] = String(i);
      this.element.appendChild(canvas);
      result.push(canvas);
    }

    return result;
  }

  private async _renderPage(
    pageNumber: number,
    canvas: HTMLCanvasElement
  ): Promise<void> {
    if (!this._doc) return;

    const page = await this._doc.getPage(pageNumber);
    const viewport = page.getViewport({ scale: this._scale });

    canvas.width = viewport.width;
    canvas.height = viewport.height;

    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    await page.render({ canvasContext: ctx, viewport }).promise;
    page.cleanup();
  }
}
