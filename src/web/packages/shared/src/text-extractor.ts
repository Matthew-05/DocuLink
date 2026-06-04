import type { CharacterEntry } from "./char-entries.js";
import type { NormalizedRect } from "./types.js";

const MIN_CHAR_BOX_OVERLAP = 0.3;

function charBoxOverlapsRect(
  aLeft: number,
  aTop: number,
  aRight: number,
  aBottom: number,
  rect: NormalizedRect,
): boolean {
  const rectRight  = rect.x + rect.width;
  const rectBottom = rect.y + rect.height;

  const intersectLeft   = Math.max(aLeft, rect.x);
  const intersectTop    = Math.max(aTop, rect.y);
  const intersectRight  = Math.min(aRight, rectRight);
  const intersectBottom = Math.min(aBottom, rectBottom);

  if (intersectLeft >= intersectRight || intersectTop >= intersectBottom) return false;

  const charArea = (aRight - aLeft) * (aBottom - aTop);
  if (charArea <= 0) return false;

  const intersectArea = (intersectRight - intersectLeft) * (intersectBottom - intersectTop);
  return intersectArea / charArea >= MIN_CHAR_BOX_OVERLAP;
}

export function extractText(entries: CharacterEntry[] | null, rect: NormalizedRect): string {
  if (!entries || entries.length === 0) return "";

  const included: CharacterEntry[] = [];
  for (const entry of entries) {
    if (charBoxOverlapsRect(entry.normLeft, entry.normTop, entry.normRight, entry.normBottom, rect)) {
      included.push(entry);
    }
  }

  if (included.length === 0) return "";

  const spacesPrecomputed = entries[0]?.spacesPrecomputed === true;
  let result = "";
  let prev: CharacterEntry | null = null;

  for (const entry of included) {
    if (prev !== null && entry.lineIndex !== prev.lineIndex) {
      result += " ";
    } else if (!spacesPrecomputed && prev !== null && entry.itemIndex !== prev.itemIndex) {
      const prevCharWidth = prev.normRight - prev.normLeft;
      const gap = entry.normLeft - prev.normRight;
      if (gap > prevCharWidth) result += " ";
    }
    result += entry.char;
    prev = entry;
  }

  return result;
}
