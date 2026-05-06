import type * as pdfjsLib from "pdfjs-dist";
import type { ZoomLevel } from "../../types/index.js";
import { loadPdfDocument } from "./pdf-loader.js";
import { renderPage } from "./page-renderer.js";
import { ensureCanvases } from "./canvas-manager.js";

export class PdfViewer {
  readonly element: HTMLElement;

  private _doc: pdfjsLib.PDFDocumentProxy | null = null;
  private _scale: ZoomLevel = 1.0;
  private _renderingQueue: Promise<void> = Promise.resolve();

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
    this.element.replaceChildren();

    this._doc = await loadPdfDocument(url, this._doc);

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

    const canvases = ensureCanvases(this.element, this._doc.numPages);

    for (let i = 1; i <= this._doc.numPages; i++) {
      const canvas = canvases[i - 1];
      if (!canvas) continue;
      await renderPage(this._doc, i, canvas, this._scale);
    }
  }
}
