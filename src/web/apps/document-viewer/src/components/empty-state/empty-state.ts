import {
  createDocumentIcon,
  createManageFilesIcon,
} from "../icons/doculink-icons.js";

export function createEmptyState(onManageFiles: () => void): HTMLElement {
  const emptyState = document.createElement("section");
  emptyState.className = "viewer-empty-state";
  emptyState.setAttribute("aria-labelledby", "viewer-empty-state-title");

  const illustration = document.createElement("div");
  illustration.className = "viewer-empty-state__illustration";
  illustration.appendChild(createDocumentIcon());

  const title = document.createElement("h1");
  title.id = "viewer-empty-state-title";
  title.className = "viewer-empty-state__title";
  title.textContent = "Bring your documents into view";

  const description = document.createElement("p");
  description.className = "viewer-empty-state__description";
  description.textContent =
    "Add a document to view its pages, search its text, and link content directly to Excel.";

  const manageFiles = document.createElement("button");
  manageFiles.type = "button";
  manageFiles.className = "viewer-empty-state__action";
  manageFiles.append(createManageFilesIcon(), "Manage Files");
  manageFiles.addEventListener("click", onManageFiles);

  const hint = document.createElement("p");
  hint.className = "viewer-empty-state__hint";
  hint.textContent = "Add or import documents for this workbook";

  emptyState.append(illustration, title, description, manageFiles, hint);
  return emptyState;
}
