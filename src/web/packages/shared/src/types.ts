export interface NormalizedRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface SearchMatch {
  id: string;
  pdfId: string;
  pdfName: string;
  pageIndex: number;
  contextText: string;
  matchInContext: { start: number; end: number };
  highlightRect: NormalizedRect;
}
