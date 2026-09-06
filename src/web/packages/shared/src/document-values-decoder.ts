export type ValueKind = "number" | "percent" | "date";
export type NoiseKind = ValueKind | "text";

export interface SpanBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface SpanSegment {
  pageIndex: number;
  text: string;
  bounds: SpanBounds;
}

/** A span that measures. Always a click target. */
export interface DetectedValue {
  id: string;
  kind: ValueKind;
  text: string;
  bounds: SpanBounds;
  confidence: number;
  normalizedValue?: string;
  magnitude?: 1000 | 1000000 | 1000000000 | 1000000000000;
  segments?: SpanSegment[];
  currency?: string;
  datePrecision?: "day" | "month" | "quarter" | "year";
  dateOrder?: "mdy" | "dmy" | "ymd" | "ambiguous";
}

/**
 * Every kind of reference the detector may publish, mirroring the closed enum
 * in `contracts/document-values-v1.json`.
 */
export const REFERENCE_KINDS = [
  "identifier",
  "phone",
  "postal",
  "tax-id",
  "security-id",
  "note",
  "item",
] as const;

export type ReferenceKind = (typeof REFERENCE_KINDS)[number];

/**
 * A span that identifies rather than measures — an invoice number, an area
 * code, a citation naming a note. A click target exactly as a value is, on
 * every document alike: an auditor sampling invoices captures the invoice
 * number as readily as the amount.
 */
export interface DetectedReference {
  id: string;
  kind: ReferenceKind;
  text: string;
  bounds: SpanBounds;
  segments?: SpanSegment[];
}

export interface ValueContext {
  currency?: string;
  scale?: 1 | 1000 | 1000000 | 1000000000;
}

/**
 * Every rule the detector may refuse a span with, mirroring the closed enum in
 * `contracts/document-values-v1.json`.
 *
 * The type is derived from this list rather than written out beside it. Held as
 * two declarations they drift, and the drift is silent: a reason the compiler
 * knows and the decoder does not is dropped by `parseNoise`, so the spans
 * disappear from the overlay with nothing logged and no type error to catch it.
 */
export const NOISE_REASONS = [
  "partial-token",
  "page-furniture",
  "note-header",
  "item-header",
  "item-toc-entry",
  "list-marker",
  "footnote-marker",
  "footnote-reference",
  "superscript",
  "citation-year",
] as const;

export type NoiseReason = (typeof NOISE_REASONS)[number];

/**
 * A span refused as neither value nor reference, with the rule that refused it.
 * Diagnostics only — noise is never a click target and must not be treated as
 * data.
 */
export interface NoiseSpan {
  id: string;
  kind: NoiseKind;
  text: string;
  bounds: SpanBounds;
  reason: NoiseReason;
}

export interface PageValues {
  pageIndex: number;
  context: ValueContext;
  values: DetectedValue[];
  references: DetectedReference[];
  noise: NoiseSpan[];
}

export interface DocumentValues {
  version: 1;
  coordinateSpace: "normalized";
  detectorVersion: string;
  documentContext: ValueContext;
  pages: PageValues[];
}

const NOISE_REASON_SET = new Set<string>(NOISE_REASONS);
const REFERENCE_KIND_SET = new Set<string>(REFERENCE_KINDS);
const CURRENCIES = new Set(["USD", "EUR", "GBP", "JPY", "CAD", "AUD", "CHF", "CNY", "INR", "KRW"]);
const PRECISIONS = new Set(["day", "month", "quarter", "year"]);
const DATE_ORDERS = new Set(["mdy", "dmy", "ymd", "ambiguous"]);
const SCALES = new Set([1, 1000, 1000000, 1000000000]);
const MAGNITUDES = new Set([1000, 1000000, 1000000000, 1000000000000]);

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function unit(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 1;
}

export function parseContext(value: unknown): ValueContext {
  if (!isRecord(value)) return {};
  const result: ValueContext = {};
  if (typeof value.currency === "string" && CURRENCIES.has(value.currency)) result.currency = value.currency;
  if (typeof value.scale === "number" && SCALES.has(value.scale)) {
    result.scale = value.scale as 1 | 1000 | 1000000 | 1000000000;
  }
  return result;
}

export function parseBounds(value: unknown): SpanBounds | null {
  if (!isRecord(value)) return null;
  const { x, y, width, height } = value;
  if (!unit(x) || !unit(y) || !unit(width) || !unit(height) || width <= 0 || height <= 0) return null;
  if (x + width > 1.005 || y + height > 1.005) return null;
  return { x, y, width, height };
}

export function parseSegment(value: unknown): SpanSegment | null {
  if (!isRecord(value) || !Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  const bounds = parseBounds(value.bounds);
  return bounds ? { pageIndex: value.pageIndex as number, text: value.text, bounds } : null;
}

function parseSegments(value: unknown): SpanSegment[] | null {
  if (!Array.isArray(value)) return null;
  const segments = value.map(parseSegment).filter((entry): entry is SpanSegment => entry !== null);
  return segments.length >= 2 ? segments : null;
}

function parseValue(value: unknown): DetectedValue | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (value.kind !== "number" && value.kind !== "percent" && value.kind !== "date") return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.confidence !== "number" || !unit(value.confidence)) return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  const parsed: DetectedValue = {
    id: value.id,
    kind: value.kind,
    text: value.text,
    bounds,
    confidence: value.confidence,
  };
  if (typeof value.normalizedValue === "string") parsed.normalizedValue = value.normalizedValue;
  if (typeof value.magnitude === "number" && MAGNITUDES.has(value.magnitude)) {
    parsed.magnitude = value.magnitude as NonNullable<DetectedValue["magnitude"]>;
  }
  const segments = parseSegments(value.segments);
  if (segments) parsed.segments = segments;
  if (typeof value.currency === "string" && CURRENCIES.has(value.currency)) parsed.currency = value.currency;
  if (typeof value.datePrecision === "string" && PRECISIONS.has(value.datePrecision)) {
    parsed.datePrecision = value.datePrecision as "day" | "month" | "quarter" | "year";
  }
  if (typeof value.dateOrder === "string" && DATE_ORDERS.has(value.dateOrder)) {
    parsed.dateOrder = value.dateOrder as "mdy" | "dmy" | "ymd" | "ambiguous";
  }
  return parsed;
}

function parseReference(value: unknown): DetectedReference | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (typeof value.kind !== "string" || !REFERENCE_KIND_SET.has(value.kind)) return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  const parsed: DetectedReference = {
    id: value.id,
    kind: value.kind as ReferenceKind,
    text: value.text,
    bounds,
  };
  const segments = parseSegments(value.segments);
  if (segments) parsed.segments = segments;
  return parsed;
}

function parseNoise(value: unknown): NoiseSpan | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (value.kind !== "number" && value.kind !== "percent" && value.kind !== "date" && value.kind !== "text") return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.reason !== "string" || !NOISE_REASON_SET.has(value.reason)) return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  return {
    id: value.id,
    kind: value.kind as NoiseKind,
    text: value.text,
    bounds,
    reason: value.reason as NoiseReason,
  };
}

export function parseDocumentValues(value: unknown): DocumentValues {
  if (!isRecord(value) || value.version !== 1 || value.coordinateSpace !== "normalized") {
    throw new Error("Unsupported document-values payload");
  }
  if (typeof value.detectorVersion !== "string" || !Array.isArray(value.pages)) {
    throw new Error("Malformed document-values payload");
  }
  const pages: PageValues[] = [];
  for (const page of value.pages) {
    if (!isRecord(page) || !Number.isInteger(page.pageIndex) || (page.pageIndex as number) < 0 || !Array.isArray(page.values)) continue;
    pages.push({
      pageIndex: page.pageIndex as number,
      context: parseContext(page.context),
      values: page.values.map(parseValue).filter((entry): entry is DetectedValue => entry !== null),
      references: Array.isArray(page.references)
        ? page.references.map(parseReference).filter((entry): entry is DetectedReference => entry !== null)
        : [],
      noise: Array.isArray(page.noise)
        ? page.noise.map(parseNoise).filter((entry): entry is NoiseSpan => entry !== null)
        : [],
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

export function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

export async function inflateJson(base64: string): Promise<unknown> {
  const compressed = base64ToBytes(base64);
  const stream = new Blob([compressed.buffer as ArrayBuffer]).stream().pipeThrough(new DecompressionStream("gzip"));
  return JSON.parse(await new Response(stream).text());
}

export async function decodeDocumentValues(base64: string): Promise<DocumentValues> {
  return parseDocumentValues(await inflateJson(base64));
}
