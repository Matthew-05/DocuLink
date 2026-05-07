import type { CharacterEntry } from "./text-content-cache.js";
import type { NormalizedRect } from "../types/index.js";

/**
 * Extracts and joins the text whose characters fall within `rect`.
 *
 * Inclusion criterion: a character is included when its centre point lies
 * inside the selection rectangle. This is selection-direction-agnostic and
 * consistent regardless of whether the PDF uses native text or OCR output.
 *
 * Joining: characters from the same TextItem are concatenated directly.
 * A space is inserted between adjacent items when the gap between the
 * right edge of the previous item and the left edge of the next exceeds
 * one estimated character-width, preventing words from being smashed
 * together or producing redundant spaces.
 */
export function extractText(
  entries: CharacterEntry[] | null,
  rect: NormalizedRect,
): string {
  if (!entries || entries.length === 0) return "";

  const rectRight  = rect.x + rect.width;
  const rectBottom = rect.y + rect.height;

  const included: CharacterEntry[] = [];

  for (const entry of entries) {
    const cx = (entry.normLeft + entry.normRight) / 2;
    const cy = (entry.normTop  + entry.normBottom) / 2;

    if (cx >= rect.x && cx <= rectRight && cy >= rect.y && cy <= rectBottom) {
      included.push(entry);
    }
  }

  if (included.length === 0) return "";

  let result = "";
  let prev: CharacterEntry | null = null;

  for (const entry of included) {
    if (prev !== null && entry.itemIndex !== prev.itemIndex) {
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
