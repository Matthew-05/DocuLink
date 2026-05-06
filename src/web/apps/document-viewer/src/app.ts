import { createToolbar } from "./components/toolbar/index.js";
import { PdfViewer } from "./components/viewer/pdf-viewer.js";

export class App {
  private readonly _root: HTMLElement;

  constructor(root: HTMLElement) {
    this._root = root;
    this._build();
  }

  private _build(): void {
    const { element: toolbarEl, zoom, page, selector } = createToolbar();
    const viewer = new PdfViewer();

    viewer.onLoaded((total) => {
      page.setTotal(total);
      page.setCurrentPage(1);
    });

    zoom.onChange((scale) => {
      viewer.setZoom(scale);
    });

    page.onChange((pageNum) => {
      viewer.scrollToPage(pageNum);
    });

    selector.onSelect((entry) => {
      void viewer.loadDocument(entry.url);
    });

    this._root.append(toolbarEl, viewer.element);
  }
}
