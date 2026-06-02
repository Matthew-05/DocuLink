export interface PdfEntry {
  id: string;
  name: string;
  /** URL or object URL pointing to the PDF data. */
  url: string;
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

export type LinkType = "auto" | "raw" | "sum";

export interface LinkRectPayload {
  pdfId: string;
  page: number; // 0-based
  rect: NormalizedRect;
  text: string;
  linkType?: LinkType;
  appendToActiveSum?: boolean;
}

export interface LinkRectUpdatedPayload extends LinkRectPayload {
  id: string;
}

export interface LinkedRectEntry {
  id: string;
  pdfId: string;
  page: number; // 0-based
  rect: NormalizedRect;
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
