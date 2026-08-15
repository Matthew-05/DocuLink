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

export function buildCharEntriesFromGeometry(geometry: TextGeometry): Map<number, CharacterEntry[]> {
  const pageMap = new Map<number, CharacterEntry[]>();

  for (const page of geometry.pages) {
    const entries: CharacterEntry[] = [];

    for (let index = 0; index < page.characters.length; index++) {
      const box = page.characters[index];
      if (box === undefined) continue;
      const entry: CharacterEntry = {
        char: box.char,
        normLeft: box.x,
        normTop: box.y,
        normRight: box.x + box.width,
        normBottom: box.y + box.height,
        lineIndex: box.lineIndex,
        itemIndex: index,
        spacesPrecomputed: true,
      };

      entries.push(entry);
    }

    pageMap.set(page.pageIndex, entries);
  }

  return pageMap;
}
