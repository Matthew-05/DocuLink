import type { DetectedValue, SpanBounds } from "@doculink/shared";

/**
 * Returns the rectangle used when creating a link from a detected value.
 * Wrapped dates are linked as one visual block; other segmented values retain
 * the bounds of the segment the user clicked.
 */
export function getValueLinkBounds(
  value: DetectedValue,
  pageIndex: number,
): SpanBounds {
  const segments = value.segments;
  if (
    value.kind !== "date"
    || !segments
    || segments.length < 2
    || segments.some((segment) => segment.pageIndex !== pageIndex)
  ) {
    return value.bounds;
  }

  const left = Math.min(...segments.map((segment) => segment.bounds.x));
  const top = Math.min(...segments.map((segment) => segment.bounds.y));
  const right = Math.max(
    ...segments.map((segment) => segment.bounds.x + segment.bounds.width),
  );
  const bottom = Math.max(
    ...segments.map((segment) => segment.bounds.y + segment.bounds.height),
  );

  return {
    x: left,
    y: top,
    width: right - left,
    height: bottom - top,
  };
}
