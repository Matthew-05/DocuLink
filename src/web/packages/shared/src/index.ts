export type { NormalizedRect, SearchMatch } from "./types.js";
export type { TextGeometry, TextGeometryPage, TextGeometryCharacter } from "./geometry-decoder.js";
export { decodeTextGeometry } from "./geometry-decoder.js";
export type {
  DetectedTable,
  PageTables,
  TableBounds,
  TableColumn,
  TableRow,
  TableHeader,
  TablePeriod,
  TableStructure,
  TableTextLine,
} from "./table-structure-decoder.js";
export { decodeTableStructure, parseTableStructure } from "./table-structure-decoder.js";
export type {
  FinancialValue,
  FsNoiseKind,
  FsNoiseReason,
  FsNoiseValue,
  FsNote,
  FsNoteHeader,
  FsNoteReference,
  FsValueSegment,
  FsValueBounds,
  FsValueContext,
  FsValueKind,
  FsValues,
  PageFsValues,
} from "./fs-values-decoder.js";
export { decodeFsValues, parseFsValues } from "./fs-values-decoder.js";
export { describeFsNoise, describeFsNoteHeader, describeFsValue } from "./fs-values-describe.js";
export type { HoverTipContent, HoverTipOptions } from "./hover-tip.js";
export { HoverTip } from "./hover-tip.js";
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
export { normalizeExtractedZeroPlaceholder } from "./zero-placeholder.js";
export type { ModalAction, ModalActionVariant, ModalOptions } from "./modal.js";
export { Modal } from "./modal.js";
export {
  encodeTextGeometry,
  extractTextGeometryFromPdfBase64,
  extractTextGeometryFromPdfDocument,
  extractTextGeometryFromPdfUrl,
} from "./pdf-text-geometry.js";
