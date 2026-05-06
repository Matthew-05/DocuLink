import { ZoomController } from "./zoom-controller.js";
import { PageController } from "./page-controller.js";
import { PdfSelector } from "./pdf-selector.js";

export interface ToolbarComponents {
  zoom: ZoomController;
  page: PageController;
  selector: PdfSelector;
}

export function createToolbar(): { element: HTMLElement } & ToolbarComponents {
  const element = document.createElement("div");
  element.className = "toolbar";

  const zoom = new ZoomController();
  const page = new PageController();
  const selector = new PdfSelector();

  element.append(zoom.element, page.element, selector.element);

  return { element, zoom, page, selector };
}
