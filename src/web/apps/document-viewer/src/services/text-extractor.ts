import type { CharacterEntry } from "./text-content-cache.js";
import type { NormalizedRect } from "../types/index.js";

function rectsIntersect(aLeft: number, aTop: number, aRight: number, aBottom: number, rect: NormalizedRect): boolean {
  const rectRight  = rect.x + rect.width;
  const rectBottom = rect.y + rect.height;
  return aRight >= rect.x && aLeft <= rectRight && aBottom >= rect.y && aTop <= rectBottom;
}

function readingOrder(a: CharacterEntry, b: CharacterEntry): number {
  if (a.normTop !== b.normTop) return a.normTop - b.normTop;
  return a.normLeft - b.normLeft;
}

/**
 * Extracts and joins the text whose character boxes intersect `rect`.
 *
 * Inclusion criterion: a character is included when its bounding box intersects
 * the selection rectangle, enabling partial-word selection.
 *
 * Stored geometry includes literal space characters from the PDF text layer.
 * The pdf.js fallback infers spaces between TextItems when the horizontal gap
 * exceeds one estimated character-width.
 */
export function extractText(
  entries: CharacterEntry[] | null,
  rect: NormalizedRect,
): string {
  if (!entries || entries.length === 0) return "";

  const included: CharacterEntry[] = [];

  for (const entry of entries) {
    if (rectsIntersect(entry.normLeft, entry.normTop, entry.normRight, entry.normBottom, rect)) {
      included.push(entry);
    }
  }

  if (included.length === 0) return "";

  const spacesPrecomputed = entries[0]?.spacesPrecomputed === true;
  const ordered = spacesPrecomputed ? [...included].sort(readingOrder) : included;

  let result = "";
  let prev: CharacterEntry | null = null;

  for (const entry of ordered) {
    if (!spacesPrecomputed && prev !== null && entry.itemIndex !== prev.itemIndex) {
      const prevCharWidth = prev.normRight - prev.normLeft;
      const gap = entry.normLeft - prev.normRight;
      if (gap > prevCharWidth) {
        result += " ";
      }
    }
    result += entry.char;
    prev = entry;
  }

  return result;
}
