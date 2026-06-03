export interface KeyColumnInfo {
  colNumber: number;
  header: string;
  rangeAddress: string;
}

export interface OutputColumnInfo {
  colNumber: number;
  header: string;
}

export interface FolderInfo {
  id: string;
  name: string;
}

export interface MatcherRow {
  rowIndex: number;
  keyValues: string[];
}

export interface MatcherPdf {
  id: string;
  name: string;
  folderId: string;
  geometryBase64: string | null;
}

export interface LinkCreationRequest {
  rowIndex: number;
  outputColNumber: number;
  pdfId: string;
  pageIndex: number;
  rect: { x: number; y: number; width: number; height: number };
  text: string;
}

export interface RowResult {
  rowIndex: number;
  status: "matched" | "unmatched" | "skipped";
  pdfName?: string;
  linkCount: number;
}

export interface SelectionInfo {
  rangeDisplay: string;
  rowCount: number;
  keyColumns: KeyColumnInfo[];
  outputColumns: OutputColumnInfo[];
}

export interface MatcherReadyPayload extends SelectionInfo {
  folders: FolderInfo[];
}

export interface MatcherDataLoadedPayload {
  rows: MatcherRow[];
  pdfs: MatcherPdf[];
}
