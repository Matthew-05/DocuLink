import type { TextGeometry } from "./geometry-decoder.js";

export interface CharacterEntry {
  char: string;
  normLeft: number;
  normTop: number;
  normRight: number;
  normBottom: number;
  lineIndex: number;
  itemIndex: number;
  spacesPrecomputed?: boolean;
}

function isNewLine(prev: CharacterEntry, nextTop: number): boolean {
  const prevHeight = prev.normBottom - prev.normTop;
  const threshold = Math.max(prevHeight * 0.5, 0.001);
  return nextTop - prev.normTop > threshold;
}

export function buildCharEntriesFromGeometry(geometry: TextGeometry): Map<number, CharacterEntry[]> {
  const pageMap = new Map<number, CharacterEntry[]>();

  for (const page of geometry.pages) {
    const entries: CharacterEntry[] = [];
    let lineIndex = 0;
    let prev: CharacterEntry | null = null;

    for (let index = 0; index < page.characters.length; index++) {
      const box = page.characters[index];
      if (box === undefined) continue;
      const entry: CharacterEntry = {
        char: box.char,
        normLeft: box.x,
        normTop: box.y,
        normRight: box.x + box.width,
        normBottom: box.y + box.height,
        lineIndex,
        itemIndex: index,
        spacesPrecomputed: true,
      };

      if (prev !== null && isNewLine(prev, entry.normTop)) {
        lineIndex++;
        entry.lineIndex = lineIndex;
      }

      entries.push(entry);
      prev = entry;
    }

    pageMap.set(page.pageIndex, entries);
  }

  return pageMap;
}
