import {
  inflateJson,
  isRecord,
  parseBounds,
  parseSegment,
  type SpanBounds,
  type SpanSegment,
} from "./document-values-decoder.js";

/** What the document appears to be. Metadata: nothing in detection branches on it. */
export type FinancialDocumentClass = "filing" | "statement" | "neither";

/**
 * What one apparatus was looked for and what it yielded. This is what lets a
 * check tell absent from missed: a compilation has no contents page and no
 * items, and a check whose subject a document does not contain must report not
 * applicable rather than failed.
 */
export interface FinancialPresence {
  searched: boolean;
  found: number;
}

export interface FinancialApparatus {
  notes: FinancialPresence;
  items: FinancialPresence;
  contents: FinancialPresence;
  parts: FinancialPresence;
}

/** One physical occurrence of a complete heading — a note's or an item's. */
export interface FinancialHeading {
  id: string;
  pageIndex: number;
  text: string;
  bounds: SpanBounds;
  continuation: boolean;
  segments?: SpanSegment[];
}

export interface FinancialNote {
  id: string;
  identifier: string;
  description: string;
  headers: FinancialHeading[];
}

/**
 * A citation resolved to the note catalogue. Its printed text and geometry are
 * not here: they belong to the reference span named by `spanId`, in the
 * companion document-values model.
 */
export interface FinancialNoteReference {
  id: string;
  spanId: string;
  noteId: string;
  identifier: string;
  description: string;
  pageIndex: number;
  descriptionPresent: boolean;
  sourceDescription?: string;
}

/** One contents row naming a filing item, with the page number it prints. */
export interface FinancialItemTocEntry {
  id: string;
  pageIndex: number;
  text: string;
  bounds: SpanBounds;
  corroborated: boolean;
  printedPage?: string;
  segments?: SpanSegment[];
}

export interface FinancialItem {
  id: string;
  identifier: string;
  description: string;
  descriptionSource: "toc" | "heading" | "none";
  headers: FinancialHeading[];
  tocEntries: FinancialItemTocEntry[];
  part?: string;
}

export interface FinancialItemReference {
  id: string;
  spanId: string;
  itemId: string;
  identifier: string;
  description: string;
  pageIndex: number;
  descriptionPresent: boolean;
  part?: string;
  sourceDescription?: string;
}

export interface FinancialStructure {
  version: 1;
  coordinateSpace: "normalized";
  detectorVersion: string;
  documentClass: FinancialDocumentClass;
  apparatus: FinancialApparatus;
  notes: FinancialNote[];
  noteReferences: FinancialNoteReference[];
  items: FinancialItem[];
  itemReferences: FinancialItemReference[];
}

const DOCUMENT_CLASSES = new Set(["filing", "statement", "neither"]);

function parseSegments(value: unknown): SpanSegment[] | null {
  if (!Array.isArray(value)) return null;
  const segments = value.map(parseSegment).filter((entry): entry is SpanSegment => entry !== null);
  return segments.length >= 2 ? segments : null;
}

function parsePresence(value: unknown): FinancialPresence {
  if (!isRecord(value)) return { searched: false, found: 0 };
  return {
    searched: value.searched === true,
    found: Number.isInteger(value.found) && (value.found as number) >= 0 ? (value.found as number) : 0,
  };
}

function parseApparatus(value: unknown): FinancialApparatus {
  const source = isRecord(value) ? value : {};
  return {
    notes: parsePresence(source.notes),
    items: parsePresence(source.items),
    contents: parsePresence(source.contents),
    parts: parsePresence(source.parts),
  };
}

function parseHeading(value: unknown): FinancialHeading | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (!Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.continuation !== "boolean") return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  const parsed: FinancialHeading = {
    id: value.id,
    pageIndex: value.pageIndex as number,
    text: value.text,
    bounds,
    continuation: value.continuation,
  };
  const segments = parseSegments(value.segments);
  if (segments) parsed.segments = segments;
  return parsed;
}

function parseNote(value: unknown): FinancialNote | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (typeof value.identifier !== "string" || value.identifier.length === 0) return null;
  if (typeof value.description !== "string" || !Array.isArray(value.headers)) return null;
  const headers = value.headers.map(parseHeading).filter((entry): entry is FinancialHeading => entry !== null);
  if (headers.length === 0) return null;
  return { id: value.id, identifier: value.identifier, description: value.description, headers };
}

function parseNoteReference(value: unknown): FinancialNoteReference | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (typeof value.spanId !== "string" || value.spanId.length === 0) return null;
  if (typeof value.noteId !== "string" || value.noteId.length === 0) return null;
  if (typeof value.identifier !== "string" || value.identifier.length === 0) return null;
  if (typeof value.description !== "string") return null;
  if (!Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.descriptionPresent !== "boolean") return null;
  if (value.sourceDescription !== undefined && (typeof value.sourceDescription !== "string" || value.sourceDescription.length === 0)) return null;
  if (value.descriptionPresent && typeof value.sourceDescription !== "string") return null;
  return {
    id: value.id,
    spanId: value.spanId,
    noteId: value.noteId,
    identifier: value.identifier,
    description: value.description,
    pageIndex: value.pageIndex as number,
    descriptionPresent: value.descriptionPresent,
    ...(typeof value.sourceDescription === "string" ? { sourceDescription: value.sourceDescription } : {}),
  };
}

function parseItemTocEntry(value: unknown): FinancialItemTocEntry | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (!Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.corroborated !== "boolean") return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  const parsed: FinancialItemTocEntry = {
    id: value.id,
    pageIndex: value.pageIndex as number,
    text: value.text,
    bounds,
    corroborated: value.corroborated,
  };
  if (typeof value.printedPage === "string" && value.printedPage.length > 0) {
    parsed.printedPage = value.printedPage;
  }
  const segments = parseSegments(value.segments);
  if (segments) parsed.segments = segments;
  return parsed;
}

function parseItem(value: unknown): FinancialItem | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (typeof value.identifier !== "string" || value.identifier.length === 0) return null;
  if (typeof value.description !== "string") return null;
  if (value.descriptionSource !== "toc" && value.descriptionSource !== "heading" && value.descriptionSource !== "none") return null;
  if (!Array.isArray(value.headers) || !Array.isArray(value.tocEntries)) return null;
  const headers = value.headers.map(parseHeading).filter((entry): entry is FinancialHeading => entry !== null);
  const tocEntries = value.tocEntries
    .map(parseItemTocEntry)
    .filter((entry): entry is FinancialItemTocEntry => entry !== null);
  // An item is where the document says it is. One with neither a heading nor a
  // contents row has no place on any page and cannot be drawn.
  if (headers.length === 0 && tocEntries.length === 0) return null;
  return {
    id: value.id,
    identifier: value.identifier,
    description: value.description,
    descriptionSource: value.descriptionSource,
    headers,
    tocEntries,
    ...(typeof value.part === "string" && value.part.length > 0 ? { part: value.part } : {}),
  };
}

function parseItemReference(value: unknown): FinancialItemReference | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (typeof value.spanId !== "string" || value.spanId.length === 0) return null;
  if (typeof value.itemId !== "string" || value.itemId.length === 0) return null;
  if (typeof value.identifier !== "string" || value.identifier.length === 0) return null;
  if (typeof value.description !== "string") return null;
  if (!Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.descriptionPresent !== "boolean") return null;
  if (value.sourceDescription !== undefined && (typeof value.sourceDescription !== "string" || value.sourceDescription.length === 0)) return null;
  if (value.descriptionPresent && typeof value.sourceDescription !== "string") return null;
  return {
    id: value.id,
    spanId: value.spanId,
    itemId: value.itemId,
    identifier: value.identifier,
    description: value.description,
    pageIndex: value.pageIndex as number,
    descriptionPresent: value.descriptionPresent,
    ...(typeof value.part === "string" && value.part.length > 0 ? { part: value.part } : {}),
    ...(typeof value.sourceDescription === "string" ? { sourceDescription: value.sourceDescription } : {}),
  };
}

export function parseFinancialStructure(value: unknown): FinancialStructure {
  if (!isRecord(value) || value.version !== 1 || value.coordinateSpace !== "normalized") {
    throw new Error("Unsupported financial-structure payload");
  }
  if (
    typeof value.detectorVersion !== "string"
    || typeof value.documentClass !== "string"
    || !DOCUMENT_CLASSES.has(value.documentClass)
    || !Array.isArray(value.notes)
    || !Array.isArray(value.noteReferences)
    || !Array.isArray(value.items)
    || !Array.isArray(value.itemReferences)
  ) {
    throw new Error("Malformed financial-structure payload");
  }
  const notes = value.notes.map(parseNote).filter((entry): entry is FinancialNote => entry !== null);
  const noteIds = new Set(notes.map((note) => note.id));
  const noteReferences = value.noteReferences
    .map(parseNoteReference)
    .filter((entry): entry is FinancialNoteReference => entry !== null && noteIds.has(entry.noteId));
  const items = value.items.map(parseItem).filter((entry): entry is FinancialItem => entry !== null);
  const itemIds = new Set(items.map((item) => item.id));
  const itemReferences = value.itemReferences
    .map(parseItemReference)
    .filter((entry): entry is FinancialItemReference => entry !== null && itemIds.has(entry.itemId));
  return {
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: value.detectorVersion,
    documentClass: value.documentClass as FinancialDocumentClass,
    apparatus: parseApparatus(value.apparatus),
    notes,
    noteReferences,
    items,
    itemReferences,
  };
}

export async function decodeFinancialStructure(base64: string): Promise<FinancialStructure> {
  return parseFinancialStructure(await inflateJson(base64));
}
