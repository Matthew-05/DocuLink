import type { PdfViewer } from "./pdf-viewer.js";
import type { PdfSelector } from "../toolbar/pdf-selector.js";
import type { SearchMatchRenderer } from "./search-match-renderer.js";
import type { SearchMatch } from "../../types/index.js";

/**
 * Returns a handler that navigates the viewer to a specific search match.
 *
 * - If the match's PDF is not active, switches to it first (awaiting full render).
 * - Shows only the clicked match on the target PDF.
 * - Applies page-fit zoom if the match is off-screen (cross-PDF always, same-PDF if not visible).
 * - Scrolls the page (and match rect if needed) into view.
 */
export function createSearchNavigator(
  viewer: PdfViewer,
  selector: PdfSelector,
  matchRenderer: SearchMatchRenderer,
  onApplyZoom?: (scale: number) => void,
): (match: SearchMatch, allMatches: SearchMatch[]) => void {
  return (match, allMatches) => {
    void _navigate(viewer, selector, matchRenderer, match, allMatches, onApplyZoom);
  };
}

async function _navigate(
  viewer: PdfViewer,
  selector: PdfSelector,
  matchRenderer: SearchMatchRenderer,
  match: SearchMatch,
  allMatches: SearchMatch[],
  onApplyZoom?: (scale: number) => void,
): Promise<void> {
  const isCrossPdf = viewer.getActivePdfId() !== match.pdfId;

  if (isCrossPdf) {
    const entry = selector.getEntry(match.pdfId);
    if (!entry) return;
    selector.setActiveId(match.pdfId);
    await viewer.loadDocument(entry.url, match.pdfId, match.pageIndex + 1);
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

  // Determine if zoom is needed: cross-PDF always, same-PDF only if not fully visible
  let shouldZoom = isCrossPdf;
  if (!isCrossPdf && matchEl) {
    const viewerRect = viewer.element.getBoundingClientRect();
    const targetRect = matchEl.getBoundingClientRect();
    const fullyVisible = targetRect.top    >= viewerRect.top
      && targetRect.bottom <= viewerRect.bottom
      && targetRect.left   >= viewerRect.left
      && targetRect.right  <= viewerRect.right;
    shouldZoom = !fullyVisible;
  }

  // Apply zoom if needed
  if (shouldZoom && onApplyZoom) {
    const fitScale = viewer.getPageFitScale(match.pageIndex + 1);
    if (fitScale !== null) {
      onApplyZoom(fitScale);
    }
  }

  await viewer.renderPageNow(match.pageIndex + 1);

  pageWrapper.scrollIntoView({ behavior: "instant" as ScrollBehavior });
}
