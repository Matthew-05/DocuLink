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

export interface LinkerRow {
  rowIndex: number;
  keyValues: string[];
}

export interface LinkerPdf {
  id: string;
  name: string;
  folderId: string;
  geometryBase64: string | null;
  base64: string | null;
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
  rowCount: number;
  keyColumns: KeyColumnInfo[];
  outputColumns: OutputColumnInfo[];
}

export interface LinkerReadyPayload extends SelectionInfo {
  folders: FolderInfo[];
}

export interface LinkerDataLoadedPayload {
  rows: LinkerRow[];
  pdfs: LinkerPdf[];
}
