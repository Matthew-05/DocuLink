export type { NormalizedRect, SearchMatch } from "./types.js";
export type { TextGeometry, TextGeometryPage, TextGeometryCharacter } from "./geometry-decoder.js";
export { decodeTextGeometry } from "./geometry-decoder.js";
export type { CharacterEntry } from "./char-entries.js";
export type { SearchPageIndex, SearchPageOptions } from "./text-searcher.js";
export { buildCharEntriesFromGeometry } from "./char-entries.js";
export {
  buildSearchPageIndex,
  buildSearchPageIndexFromEntries,
  cleanAutoInsertedSearchQuery,
  normalizeMatcherQuery,
  normalizeSearchQuery,
  pageTextMatchesQuery,
  searchPage,
  searchPageWithIndex,
} from "./text-searcher.js";
export { extractText } from "./text-extractor.js";
export type { ModalAction, ModalActionVariant, ModalOptions } from "./modal.js";
export { Modal } from "./modal.js";
export {
  encodeTextGeometry,
  extractTextGeometryFromPdfBase64,
  extractTextGeometryFromPdfDocument,
  extractTextGeometryFromPdfUrl,
} from "./pdf-text-geometry.js";
