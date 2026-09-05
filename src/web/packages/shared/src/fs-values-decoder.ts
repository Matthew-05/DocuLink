export type FsValueKind = "number" | "percent" | "date";
export type FsNoiseKind = FsValueKind | "text";

export interface FsValueBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FsValueSegment {
  pageIndex: number;
  text: string;
  bounds: FsValueBounds;
}

export interface FinancialValue {
  id: string;
  kind: FsValueKind;
  text: string;
  bounds: FsValueBounds;
  confidence: number;
  normalizedValue?: string;
  magnitude?: 1000 | 1000000 | 1000000000 | 1000000000000;
  segments?: FsValueSegment[];
  currency?: string;
  datePrecision?: "day" | "month" | "quarter" | "year";
  dateOrder?: "mdy" | "dmy" | "ymd" | "ambiguous";
}

export interface FsValueContext {
  currency?: string;
  scale?: 1 | 1000 | 1000000 | 1000000000;
}

export type FsNoiseReason =
  | "identifier"
  | "alphanumeric"
  | "partial-token"
  | "page-furniture"
  | "note-header"
  | "note-reference"
  | "phone-context"
  | "identifier-context"
  | "superscript"
  | "citation-year";

/**
 * A span the detector recognized and then refused, with the rule that refused
 * it. Diagnostics only — noise is never a click target and must not be treated
 * as a value.
 */
export interface FsNoiseValue {
  id: string;
  kind: FsNoiseKind;
  text: string;
  bounds: FsValueBounds;
  reason: FsNoiseReason;
}

export interface FsNoteHeader {
  id: string;
  pageIndex: number;
  text: string;
  bounds: FsValueBounds;
  continuation: boolean;
  segments?: FsValueSegment[];
}

export interface FsNote {
  id: string;
  identifier: string;
  description: string;
  headers: FsNoteHeader[];
}

export interface FsNoteReference {
  id: string;
  noteId: string;
  identifier: string;
  description: string;
  pageIndex: number;
  text: string;
  bounds: FsValueBounds;
  descriptionPresent: boolean;
  sourceDescription?: string;
}

export interface PageFsValues {
  pageIndex: number;
  context: FsValueContext;
  values: FinancialValue[];
  noise: FsNoiseValue[];
}

export interface FsValues {
  version: 1;
  coordinateSpace: "normalized";
  detectorVersion: string;
  documentContext: FsValueContext;
  notes: FsNote[];
  noteReferences: FsNoteReference[];
  pages: PageFsValues[];
}

const NOISE_REASONS = new Set<string>([
  "identifier",
  "alphanumeric",
  "partial-token",
  "page-furniture",
  "note-header",
  "note-reference",
  "phone-context",
  "identifier-context",
  "superscript",
  "citation-year",
]);
const CURRENCIES = new Set(["USD", "EUR", "GBP", "JPY", "CAD", "AUD", "CHF", "CNY", "INR", "KRW"]);
const PRECISIONS = new Set(["day", "month", "quarter", "year"]);
const DATE_ORDERS = new Set(["mdy", "dmy", "ymd", "ambiguous"]);
const SCALES = new Set([1, 1000, 1000000, 1000000000]);
const MAGNITUDES = new Set([1000, 1000000, 1000000000, 1000000000000]);

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

function parseBounds(value: unknown): FsValueBounds | null {
  if (!isRecord(value)) return null;
  const { x, y, width, height } = value;
  if (!unit(x) || !unit(y) || !unit(width) || !unit(height) || width <= 0 || height <= 0) return null;
  if (x + width > 1.005 || y + height > 1.005) return null;
  return { x, y, width, height };
}

function parseSegment(value: unknown): FsValueSegment | null {
  if (!isRecord(value) || !Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  const bounds = parseBounds(value.bounds);
  return bounds ? { pageIndex: value.pageIndex as number, text: value.text, bounds } : null;
}

function parseValue(value: unknown): FinancialValue | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (value.kind !== "number" && value.kind !== "percent" && value.kind !== "date") return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.confidence !== "number" || !unit(value.confidence)) return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  const parsed: FinancialValue = {
    id: value.id,
    kind: value.kind,
    text: value.text,
    bounds,
    confidence: value.confidence,
  };
  if (typeof value.normalizedValue === "string") parsed.normalizedValue = value.normalizedValue;
  if (typeof value.magnitude === "number" && MAGNITUDES.has(value.magnitude)) {
    parsed.magnitude = value.magnitude as NonNullable<FinancialValue["magnitude"]>;
  }
  if (Array.isArray(value.segments)) {
    const segments = value.segments.map(parseSegment).filter((entry): entry is FsValueSegment => entry !== null);
    if (segments.length >= 2) parsed.segments = segments;
  }
  if (typeof value.currency === "string" && CURRENCIES.has(value.currency)) parsed.currency = value.currency;
  if (typeof value.datePrecision === "string" && PRECISIONS.has(value.datePrecision)) {
    parsed.datePrecision = value.datePrecision as "day" | "month" | "quarter" | "year";
  }
  if (typeof value.dateOrder === "string" && DATE_ORDERS.has(value.dateOrder)) {
    parsed.dateOrder = value.dateOrder as "mdy" | "dmy" | "ymd" | "ambiguous";
  }
  return parsed;
}

function parseNoise(value: unknown): FsNoiseValue | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (value.kind !== "number" && value.kind !== "percent" && value.kind !== "date" && value.kind !== "text") return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.reason !== "string" || !NOISE_REASONS.has(value.reason)) return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  return {
    id: value.id,
    kind: value.kind as FsNoiseKind,
    text: value.text,
    bounds,
    reason: value.reason as FsNoiseReason,
  };
}

function parseNoteHeader(value: unknown): FsNoteHeader | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (!Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.continuation !== "boolean") return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  const parsed: FsNoteHeader = {
    id: value.id,
    pageIndex: value.pageIndex as number,
    text: value.text,
    bounds,
    continuation: value.continuation,
  };
  if (Array.isArray(value.segments)) {
    const segments = value.segments.map(parseSegment).filter((entry): entry is FsValueSegment => entry !== null);
    if (segments.length >= 2) parsed.segments = segments;
  }
  return parsed;
}

function parseNote(value: unknown): FsNote | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (typeof value.identifier !== "string" || value.identifier.length === 0) return null;
  if (typeof value.description !== "string" || !Array.isArray(value.headers)) return null;
  const headers = value.headers
    .map(parseNoteHeader)
    .filter((entry): entry is FsNoteHeader => entry !== null);
  if (headers.length === 0) return null;
  return {
    id: value.id,
    identifier: value.identifier,
    description: value.description,
    headers,
  };
}

function parseNoteReference(value: unknown): FsNoteReference | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (typeof value.noteId !== "string" || value.noteId.length === 0) return null;
  if (typeof value.identifier !== "string" || value.identifier.length === 0) return null;
  if (typeof value.description !== "string") return null;
  if (!Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.descriptionPresent !== "boolean") return null;
  if (value.sourceDescription !== undefined && (typeof value.sourceDescription !== "string" || value.sourceDescription.length === 0)) return null;
  if (value.descriptionPresent && typeof value.sourceDescription !== "string") return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  return {
    id: value.id,
    noteId: value.noteId,
    identifier: value.identifier,
    description: value.description,
    pageIndex: value.pageIndex as number,
    text: value.text,
    bounds,
    descriptionPresent: value.descriptionPresent,
    ...(typeof value.sourceDescription === "string" ? { sourceDescription: value.sourceDescription } : {}),
  };
}

export function parseFsValues(value: unknown): FsValues {
  if (!isRecord(value) || value.version !== 1 || value.coordinateSpace !== "normalized") {
    throw new Error("Unsupported fs-values payload");
  }
  if (
    typeof value.detectorVersion !== "string"
    || !Array.isArray(value.notes)
    || !Array.isArray(value.noteReferences)
    || !Array.isArray(value.pages)
  ) {
    throw new Error("Malformed fs-values payload");
  }
  const notes = value.notes.map(parseNote).filter((entry): entry is FsNote => entry !== null);
  const noteIds = new Set(notes.map((note) => note.id));
  const noteReferences = value.noteReferences
    .map(parseNoteReference)
    .filter((entry): entry is FsNoteReference => entry !== null && noteIds.has(entry.noteId));
  const pages: PageFsValues[] = [];
  for (const page of value.pages) {
    if (!isRecord(page) || !Number.isInteger(page.pageIndex) || (page.pageIndex as number) < 0 || !Array.isArray(page.values)) continue;
    pages.push({
      pageIndex: page.pageIndex as number,
      context: parseContext(page.context),
      values: page.values.map(parseValue).filter((entry): entry is FinancialValue => entry !== null),
      noise: Array.isArray(page.noise)
        ? page.noise.map(parseNoise).filter((entry): entry is FsNoiseValue => entry !== null)
        : [],
    });
  }
  return {
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: value.detectorVersion,
    documentContext: parseContext(value.documentContext),
    notes,
    noteReferences,
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
