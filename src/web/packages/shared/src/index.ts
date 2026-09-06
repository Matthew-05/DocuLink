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
  DetectedReference,
  DetectedStructure,
  DetectedValue,
  DocumentValues,
  NoiseKind,
  NoiseReason,
  NoiseSpan,
  PageValues,
  ReferenceKind,
  SpanBounds,
  StructureKind,
  SpanSegment,
  ValueContext,
  ValueKind,
} from "./document-values-decoder.js";
export {
  NOISE_REASONS,
  REFERENCE_KINDS,
  STRUCTURE_KINDS,
  decodeDocumentValues,
  parseDocumentValues,
} from "./document-values-decoder.js";
export type {
  FsApparatus,
  FsDocumentClass,
  FsHeading,
  FsItem,
  FsItemReference,
  FsItemTocEntry,
  FsNote,
  FsNoteReference,
  FsPresence,
  FsStructure,
} from "./fs-structure-decoder.js";
export { decodeFsStructure, parseFsStructure } from "./fs-structure-decoder.js";
export type { SpanTipContext } from "./span-describe.js";
export {
  describeItemHeader,
  describeItemReference,
  describeItemTocEntry,
  describeNoise,
  describeNoteHeader,
  describeNoteReference,
  describeReference,
  describeStructure,
  describeValue,
} from "./span-describe.js";
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
