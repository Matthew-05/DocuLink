import { PdfViewer } from "./components/viewer/pdf-viewer.js";
import { initializeViewer } from "./components/viewer/initialize-viewer.js";

export function mountApp(root: HTMLElement): void {
  root.className = "document-viewer";
  const viewer = new PdfViewer();
  const { toolbarElement } = initializeViewer(viewer);
  root.append(toolbarElement, viewer.element);
}
