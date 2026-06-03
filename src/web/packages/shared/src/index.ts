export type { NormalizedRect, SearchMatch } from "./types.js";
export type { TextGeometry, TextGeometryPage, TextGeometryCharacter } from "./geometry-decoder.js";
export { decodeTextGeometry } from "./geometry-decoder.js";
export type { CharacterEntry } from "./char-entries.js";
export { buildCharEntriesFromGeometry } from "./char-entries.js";
export { normalizeSearchQuery, pageTextMatchesQuery, searchPage } from "./text-searcher.js";
export { extractText } from "./text-extractor.js";
