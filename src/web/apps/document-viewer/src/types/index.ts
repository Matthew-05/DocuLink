/** A named folder used to organise documents, mirrored from workbook storage. */
export interface FolderEntry {
  id: string;
  name: string;
}

export interface PdfEntry {
  id: string;
  name: string;
  /** URL or object URL pointing to the PDF data. */
  url: string;
  /** GUID of the owning folder; absent when the document is uncategorised. */
  folderId?: string | undefined;
  /** Gzip-compressed text-geometry-v1 JSON, base64-encoded. */
  geometryBase64?: string;
  linkCount?: number;
  /** Per-page clockwise rotation in degrees (0, 90, 180, 270). Absent entries default to 0. */
  pageRotations?: Record<number, number>;
}

export type ZoomLevel = number; // scale factor, e.g. 1.0 = 100%

export interface PageState {
  current: number; // 1-based
  total: number;
}

export interface NormalizedRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export type LinkType = "auto" | "raw" | "sum" | "table";

export interface TableGridData {
  /** Internal vertical boundaries as fractions of the rectangle width. */
  columnBoundaries: number[];
  /** Internal horizontal boundaries as fractions of the rectangle height. */
  rowBoundaries: number[];
  /** Row-major text extracted for the current boundaries. */
  cells?: string[][];
}

export interface LinkRectPayload {
  pdfId: string;
  page: number; // 0-based
  rect: NormalizedRect;
  text: string;
  linkType?: LinkType;
  appendToActiveSum?: boolean;
  table?: TableGridData;
}

export interface LinkRectUpdatedPayload extends LinkRectPayload {
  id: string;
}

export interface LinkedRectEntry {
  id: string;
  pdfId: string;
  page: number; // 0-based
  rect: NormalizedRect;
  linkType?: LinkType;
  table?: TableGridData;
}

/**
 * One linked rectangle inside the current Excel selection. A sum cell contributes
 * one entry per contributing rectangle, all sharing `cellAddress` and `cellValue`.
 */
export interface LinkSelectionEntry {
  id: string;
  pdfId: string;
  /** Display name of the owning PDF; shown only when the selection spans documents. */
  pdfName: string;
  page: number; // 0-based
  /** Captured text for a sum rectangle, otherwise the linked cell's displayed text. */
  value: string;
  /**
   * How many numbers this rectangle contributes to its sum cell. 1 for a single
   * value and for non-sum links; above 1 the panel shows the count, not the items.
   */
  valueCount: number;
  /** Sheet-qualified cell address, e.g. "Sheet1!B4". Entries sharing it share a cell. */
  cellAddress: string;
  /** Displayed text of the linked cell — the computed total for a sum cell. */
  cellValue: string;
}

export interface SearchMatch {
  id: string;
  pdfId: string;
  pdfName: string;
  pageIndex: number; // 0-based
  contextText: string;
  matchInContext: { start: number; end: number };
  highlightRect: NormalizedRect;
}
