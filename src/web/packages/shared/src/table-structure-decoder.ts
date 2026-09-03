export interface TableStructure {
  version: 1;
  coordinateSpace: "normalized";
  pages: PageTables[];
}

export interface PageTables {
  pageIndex: number;
  tables: DetectedTable[];
}

export interface TableBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface TableColumn {
  x0: number;
  x1: number;
}

export interface TableTextLine {
  y0: number;
  y1: number;
}

export interface TableRow {
  y0: number;
  y1: number;
  kind: "header" | "body";
  textLines: TableTextLine[];
  merged: boolean;
  mergeConfidence: number;
}

export interface DetectedTable {
  id: string;
  bounds: TableBounds;
  evidence: "ruled" | "whitespace" | "mixed";
  confidence: number;
  columns: TableColumn[];
  rows: TableRow[];
  header: { rowCount: number; labels: string[] } | null;
  rulings: { vertical: number[]; horizontal: number[] };
}

function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

export async function decodeTableStructure(base64: string): Promise<TableStructure> {
  const compressed = base64ToBytes(base64);
  const stream = new Blob([compressed.buffer as ArrayBuffer])
    .stream()
    .pipeThrough(new DecompressionStream("gzip"));
  return JSON.parse(await new Response(stream).text()) as TableStructure;
}
