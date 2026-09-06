import {
  inflateJson,
  isRecord,
  parseBounds,
  parseSegment,
  type SpanBounds,
  type SpanSegment,
} from "./document-values-decoder.js";

/** What the document appears to be. Metadata: nothing in detection branches on it. */
export type FsDocumentClass = "filing" | "statement" | "neither";

/**
 * What one apparatus was looked for and what it yielded. This is what lets a
 * check tell absent from missed: a compilation has no contents page and no
 * items, and a check whose subject a document does not contain must report not
 * applicable rather than failed.
 */
export interface FsPresence {
  searched: boolean;
  found: number;
}

export interface FsApparatus {
  notes: FsPresence;
  items: FsPresence;
  contents: FsPresence;
  parts: FsPresence;
}

/** One physical occurrence of a complete heading — a note's or an item's. */
export interface FsHeading {
  id: string;
  pageIndex: number;
  text: string;
  bounds: SpanBounds;
  continuation: boolean;
  segments?: SpanSegment[];
}

export interface FsNote {
  id: string;
  identifier: string;
  description: string;
  headers: FsHeading[];
}

/**
 * A citation resolved to the note catalogue. Its printed text and geometry are
 * not here: they belong to the reference span named by `spanId`, in the
 * companion document-values model.
 */
export interface FsNoteReference {
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
export interface FsItemTocEntry {
  id: string;
  pageIndex: number;
  text: string;
  bounds: SpanBounds;
  corroborated: boolean;
  printedPage?: string;
  segments?: SpanSegment[];
}

export interface FsItem {
  id: string;
  identifier: string;
  description: string;
  descriptionSource: "toc" | "heading" | "none";
  headers: FsHeading[];
  tocEntries: FsItemTocEntry[];
  part?: string;
}

export interface FsItemReference {
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

export interface FsStructure {
  version: 1;
  coordinateSpace: "normalized";
  detectorVersion: string;
  documentClass: FsDocumentClass;
  apparatus: FsApparatus;
  notes: FsNote[];
  noteReferences: FsNoteReference[];
  items: FsItem[];
  itemReferences: FsItemReference[];
}

const DOCUMENT_CLASSES = new Set(["filing", "statement", "neither"]);

function parseSegments(value: unknown): SpanSegment[] | null {
  if (!Array.isArray(value)) return null;
  const segments = value.map(parseSegment).filter((entry): entry is SpanSegment => entry !== null);
  return segments.length >= 2 ? segments : null;
}

function parsePresence(value: unknown): FsPresence {
  if (!isRecord(value)) return { searched: false, found: 0 };
  return {
    searched: value.searched === true,
    found: Number.isInteger(value.found) && (value.found as number) >= 0 ? (value.found as number) : 0,
  };
}

function parseApparatus(value: unknown): FsApparatus {
  const source = isRecord(value) ? value : {};
  return {
    notes: parsePresence(source.notes),
    items: parsePresence(source.items),
    contents: parsePresence(source.contents),
    parts: parsePresence(source.parts),
  };
}

function parseHeading(value: unknown): FsHeading | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (!Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.continuation !== "boolean") return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  const parsed: FsHeading = {
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

function parseNote(value: unknown): FsNote | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (typeof value.identifier !== "string" || value.identifier.length === 0) return null;
  if (typeof value.description !== "string" || !Array.isArray(value.headers)) return null;
  const headers = value.headers.map(parseHeading).filter((entry): entry is FsHeading => entry !== null);
  if (headers.length === 0) return null;
  return { id: value.id, identifier: value.identifier, description: value.description, headers };
}

function parseNoteReference(value: unknown): FsNoteReference | null {
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

function parseItemTocEntry(value: unknown): FsItemTocEntry | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (!Number.isInteger(value.pageIndex) || (value.pageIndex as number) < 0) return null;
  if (typeof value.text !== "string" || value.text.length === 0) return null;
  if (typeof value.corroborated !== "boolean") return null;
  const bounds = parseBounds(value.bounds);
  if (!bounds) return null;
  const parsed: FsItemTocEntry = {
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

function parseItem(value: unknown): FsItem | null {
  if (!isRecord(value)) return null;
  if (typeof value.id !== "string" || value.id.length === 0) return null;
  if (typeof value.identifier !== "string" || value.identifier.length === 0) return null;
  if (typeof value.description !== "string") return null;
  if (value.descriptionSource !== "toc" && value.descriptionSource !== "heading" && value.descriptionSource !== "none") return null;
  if (!Array.isArray(value.headers) || !Array.isArray(value.tocEntries)) return null;
  const headers = value.headers.map(parseHeading).filter((entry): entry is FsHeading => entry !== null);
  const tocEntries = value.tocEntries
    .map(parseItemTocEntry)
    .filter((entry): entry is FsItemTocEntry => entry !== null);
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

function parseItemReference(value: unknown): FsItemReference | null {
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

export function parseFsStructure(value: unknown): FsStructure {
  if (!isRecord(value) || value.version !== 1 || value.coordinateSpace !== "normalized") {
    throw new Error("Unsupported fs-structure payload");
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
    throw new Error("Malformed fs-structure payload");
  }
  const notes = value.notes.map(parseNote).filter((entry): entry is FsNote => entry !== null);
  const noteIds = new Set(notes.map((note) => note.id));
  const noteReferences = value.noteReferences
    .map(parseNoteReference)
    .filter((entry): entry is FsNoteReference => entry !== null && noteIds.has(entry.noteId));
  const items = value.items.map(parseItem).filter((entry): entry is FsItem => entry !== null);
  const itemIds = new Set(items.map((item) => item.id));
  const itemReferences = value.itemReferences
    .map(parseItemReference)
    .filter((entry): entry is FsItemReference => entry !== null && itemIds.has(entry.itemId));
  return {
    version: 1,
    coordinateSpace: "normalized",
    detectorVersion: value.detectorVersion,
    documentClass: value.documentClass as FsDocumentClass,
    apparatus: parseApparatus(value.apparatus),
    notes,
    noteReferences,
    items,
    itemReferences,
  };
}

export async function decodeFsStructure(base64: string): Promise<FsStructure> {
  return parseFsStructure(await inflateJson(base64));
}
