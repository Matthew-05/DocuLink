import { ZoomController } from "./zoom-controller.js";
import { PageController } from "./page-controller.js";
import { PdfSelector } from "./pdf-selector.js";
import { SearchBar } from "./search-bar.js";
import { RotateController } from "./rotate-controller.js";

export interface ToolbarComponents {
  zoom: ZoomController;
  page: PageController;
  selector: PdfSelector;
  search: SearchBar;
  rotate: RotateController;
}

export function createToolbar(): { element: HTMLElement } & ToolbarComponents {
  const element = document.createElement("div");
  element.className = "toolbar";

  const zoom = new ZoomController();
  const page = new PageController();
  const rotate = new RotateController();
  const selector = new PdfSelector();
  const search = new SearchBar();

  selector.onOpen(() => search.hideResults());
  search.onResultsShown(() => selector.close());

  const left = document.createElement("div");
  left.className = "toolbar__left";
  left.append(selector.element);

  const center = document.createElement("div");
  center.className = "toolbar__center";
  // Layout: [zoom controls] [↺↻] [page indicator]
  center.append(zoom.element, rotate.element, page.element);

  const right = document.createElement("div");
  right.className = "toolbar__right";
  right.append(search.element);

  element.append(left, center, right);

  return { element, zoom, page, selector, search, rotate };
}
