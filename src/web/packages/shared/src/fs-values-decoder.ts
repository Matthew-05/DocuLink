export type FsValueKind = "number" | "percent" | "date";

export interface FsValueBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FinancialValue {
  id: string;
  kind: FsValueKind;
  text: string;
  bounds: FsValueBounds;
  confidence: number;
  normalizedValue?: string;
  currency?: string;
  datePrecision?: "day" | "month" | "quarter" | "year";
  dateOrder?: "mdy" | "dmy" | "ymd" | "ambiguous";
}

export interface FsValueContext {
  currency?: string;
  scale?: 1 | 1000 | 1000000 | 1000000000;
}

export interface PageFsValues {
  pageIndex: number;
  context: FsValueContext;
  values: FinancialValue[];
}

export interface FsValues {
  version: 1;
  coordinateSpace: "normalized";
  detectorVersion: string;
  documentContext: FsValueContext;
  pages: PageFsValues[];
}

const CURRENCIES = new Set(["USD", "EUR", "GBP", "JPY", "CAD", "AUD", "CHF", "CNY", "INR", "KRW"]);
const PRECISIONS = new Set(["day", "month", "quarter", "year"]);
const DATE_ORDERS = new Set(["mdy", "dmy", "ymd", "ambiguous"]);
const SCALES = new Set([1, 1000, 1000000, 1000000000]);

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function unit(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 1;
}

function parseContext(value: unknown): FsValueContext {
  if (!isRecord(value)) return {};
  const result: FsValueContext = {};
  if (typeof value.currency === "string" && CURRENCIES.has(value.currency)) result.currency = value.currency;
  if (typeof value.scale === "number" && SCALES.has(value.scale)) {
    result.scale = value.scale as 1 | 1000 | 1000000 | 1000000000;
  }
  return result;
}

function parseValue(value: unknown): FinancialValue | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (value.kind !== "number" && value.kind !== "percent" && value.kind !== "date") return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.confidence !== "number" || !unit(value.confidence)) return null;
  if (!isRecord(value.bounds)) return null;
  const { x, y, width, height } = value.bounds;
  if (!unit(x) || !unit(y) || !unit(width) || !unit(height) || width <= 0 || height <= 0) return null;
  if (x + width > 1.005 || y + height > 1.005) return null;
  const parsed: FinancialValue = {
    id: value.id,
    kind: value.kind,
    text: value.text,
    bounds: { x, y, width, height },
    confidence: value.confidence,
  };
  if (typeof value.normalizedValue === "string") parsed.normalizedValue = value.normalizedValue;
  if (typeof value.currency === "string" && CURRENCIES.has(value.currency)) parsed.currency = value.currency;
  if (typeof value.datePrecision === "string" && PRECISIONS.has(value.datePrecision)) {
    parsed.datePrecision = value.datePrecision as "day" | "month" | "quarter" | "year";
  }
  if (typeof value.dateOrder === "string" && DATE_ORDERS.has(value.dateOrder)) {
    parsed.dateOrder = value.dateOrder as "mdy" | "dmy" | "ymd" | "ambiguous";
  }
  return parsed;
}

export function parseFsValues(value: unknown): FsValues {
  if (!isRecord(value) || value.version !== 1 || value.coordinateSpace !== "normalized") {
    throw new Error("Unsupported fs-values payload");
  }
  if (typeof value.detectorVersion !== "string" || !Array.isArray(value.pages)) {
    throw new Error("Malformed fs-values payload");
  }
  const pages: PageFsValues[] = [];
  for (const page of value.pages) {
    if (!isRecord(page) || !Number.isInteger(page.pageIndex) || (page.pageIndex as number) < 0 || !Array.isArray(page.values)) continue;
    pages.push({
      pageIndex: page.pageIndex as number,
      context: parseContext(page.context),
      values: page.values.map(parseValue).filter((entry): entry is FinancialValue => entry !== null),
    });
  }
  return {
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: value.detectorVersion,
    documentContext: parseContext(value.documentContext),
    pages,
  };
}

function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

export async function decodeFsValues(base64: string): Promise<FsValues> {
  const compressed = base64ToBytes(base64);
  const stream = new Blob([compressed.buffer as ArrayBuffer]).stream().pipeThrough(new DecompressionStream("gzip"));
  return parseFsValues(JSON.parse(await new Response(stream).text()));
}
