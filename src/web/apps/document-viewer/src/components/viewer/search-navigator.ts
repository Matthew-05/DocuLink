import type { PdfViewer } from "./pdf-viewer.js";
import type { PdfSelector } from "../toolbar/pdf-selector.js";
import type { SearchMatchRenderer } from "./search-match-renderer.js";
import type { SearchMatch } from "../../types/index.js";

/**
 * Returns a handler that navigates the viewer to a specific search match.
 *
 * - If the match's PDF is not active, switches to it first (awaiting full render).
 * - Shows only the clicked match on the target PDF.
 * - Scrolls the page (and match rect if needed) into view.
 */
export function createSearchNavigator(
  viewer: PdfViewer,
  selector: PdfSelector,
  matchRenderer: SearchMatchRenderer,
): (match: SearchMatch, allMatches: SearchMatch[]) => void {
  return (match, allMatches) => {
    void _navigate(viewer, selector, matchRenderer, match, allMatches);
  };
}

async function _navigate(
  viewer: PdfViewer,
  selector: PdfSelector,
  matchRenderer: SearchMatchRenderer,
  match: SearchMatch,
  allMatches: SearchMatch[],
): Promise<void> {
  if (viewer.getActivePdfId() !== match.pdfId) {
    const entry = selector.getEntry(match.pdfId);
    if (!entry) return;
    selector.setActiveId(match.pdfId);
    await viewer.loadDocument(entry.url, match.pdfId);
  }

  matchRenderer.setMatches([match]);
  matchRenderer.highlightMatch(match.id);

  const matchEl = viewer.element.querySelector<HTMLElement>(
    `[data-match-id="${CSS.escape(match.id)}"]`,
  );

  const pageWrapper = viewer.element.querySelector<HTMLElement>(
    `[data-page="${match.pageIndex + 1}"]`,
  );
  if (!pageWrapper) return;

  if (matchEl) {
    const viewerRect = viewer.element.getBoundingClientRect();
    const targetRect = matchEl.getBoundingClientRect();
    const fullyVisible = targetRect.top    >= viewerRect.top
      && targetRect.bottom <= viewerRect.bottom
      && targetRect.left   >= viewerRect.left
      && targetRect.right  <= viewerRect.right;

    if (!fullyVisible) {
      pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });
      return;
    }
  }

  pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });
}
