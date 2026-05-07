export interface PdfEntry {
  id: string;
  name: string;
  /** URL or object URL pointing to the PDF data. */
  url: string;
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

export interface LinkRectPayload {
  pdfId: string;
  page: number; // 0-based
  rect: NormalizedRect;
  text: string;
}

export interface LinkedRectEntry {
  id: string;
  pdfId: string;
  page: number; // 0-based
  rect: NormalizedRect;
}
