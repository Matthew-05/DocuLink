import { PdfViewer } from "./components/viewer/pdf-viewer.js";
import { initializeViewer } from "./components/viewer/initialize-viewer.js";

export class App {
  private readonly _root: HTMLElement;

  constructor(root: HTMLElement) {
    this._root = root;
    this._build();
  }

  private _build(): void {
    const viewer = new PdfViewer();
    const { toolbarElement } = initializeViewer(viewer);
    this._root.append(toolbarElement, viewer.element);
  }
}
