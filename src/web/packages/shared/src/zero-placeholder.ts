const ZERO_PLACEHOLDER = /^(\s*(?:\$\s*)?)—(\s*)$/u;

export function isStandaloneZeroDash(
  text: string,
  index: number,
  hasGeometryBoundary?: (leftIndex: number, rightIndex: number) => boolean,
): boolean {
  if (text[index] !== "—") return false;

  const left = text[index - 1];
  const right = text[index + 1];
  const hasLeftBoundary = left === undefined
    || left === "$"
    || /\s/u.test(left)
    || hasGeometryBoundary?.(index - 1, index) === true;
  const hasRightBoundary = right === undefined
    || /\s/u.test(right)
    || hasGeometryBoundary?.(index, index + 1) === true;
  return hasLeftBoundary && hasRightBoundary;
}

export function normalizeExtractedZeroPlaceholder(text: string): string {
  return text.replace(ZERO_PLACEHOLDER, (_match, prefix: string, suffix: string) => (
    `${prefix}0${suffix}`
  ));
}
