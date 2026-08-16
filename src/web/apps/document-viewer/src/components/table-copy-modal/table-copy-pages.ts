export type TableCopyMode = "below" | "range";

/** Resolves inclusive, 1-based UI page choices into unique 0-based target pages. */
export function resolveTableCopyPages(
  sourcePage: number,
  totalPages: number,
  mode: TableCopyMode,
  rangeStart?: number,
  rangeEnd?: number,
): number[] {
  if (!Number.isInteger(sourcePage) || !Number.isInteger(totalPages) || totalPages < 1) return [];

  if (mode === "below") {
    return Array.from(
      { length: Math.max(0, totalPages - sourcePage - 1) },
      (_, index) => sourcePage + index + 1,
    );
  }

  if (
    !Number.isInteger(rangeStart)
    || !Number.isInteger(rangeEnd)
    || rangeStart! < 1
    || rangeEnd! > totalPages
    || rangeStart! > rangeEnd!
  ) return [];

  const pages: number[] = [];
  for (let pageNumber = rangeStart!; pageNumber <= rangeEnd!; pageNumber++) {
    const pageIndex = pageNumber - 1;
    if (pageIndex !== sourcePage) pages.push(pageIndex);
  }
  return pages;
}
