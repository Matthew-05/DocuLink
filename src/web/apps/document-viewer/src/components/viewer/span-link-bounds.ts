import type {
  DetectedReference,
  DetectedStructure,
  DetectedValue,
  SpanBounds,
} from "@talliark/shared";

/**
 * A span the user clicked to create a link. Discriminated rather than widened
 * because only a value carries the segments a wrapped date needs.
 */
export type ClickableSpan =
  | { category: "value"; value: DetectedValue }
  | { category: "reference"; reference: DetectedReference }
  | { category: "structure"; structure: DetectedStructure };

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

/**
 * The rectangle and text a clicked span contributes to a link.
 *
 * One place decides this for all three categories, so a category added later
 * fails to compile here rather than silently linking the wrong rectangle.
 */
export function getSpanLinkTarget(
  span: ClickableSpan,
  pageIndex: number,
): { rect: SpanBounds; text: string } {
  switch (span.category) {
    case "value":
      // Only a value can wrap across lines, so only a value needs its segments
      // gathered back into one block.
      return { rect: getValueLinkBounds(span.value, pageIndex), text: span.value.text };
    case "reference":
      return { rect: span.reference.bounds, text: span.reference.text };
    case "structure":
      return { rect: span.structure.bounds, text: span.structure.text };
  }
}
