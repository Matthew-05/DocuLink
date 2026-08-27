export interface VerticalPageBounds {
  pageNumber: number;
  top: number;
  bottom: number;
}

export interface VisiblePageMeasurement {
  pageNumber: number;
  visibleHeight: number;
}

/** Returns every page that intersects the viewer's vertical viewport. */
export function measureVisiblePages(
  viewport: { top: number; bottom: number },
  pages: VerticalPageBounds[],
): VisiblePageMeasurement[] {
  const visiblePages: VisiblePageMeasurement[] = [];

  for (const { pageNumber, top, bottom } of pages) {
    const visibleTop = Math.max(top, viewport.top);
    const visibleBottom = Math.min(bottom, viewport.bottom);
    const visibleHeight = Math.max(0, visibleBottom - visibleTop);
    if (visibleHeight > 0) visiblePages.push({ pageNumber, visibleHeight });
  }

  return visiblePages;
}
