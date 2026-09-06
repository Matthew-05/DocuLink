import type {
  DetectedReference,
  DetectedValue,
  DocumentValues,
  FsApparatus,
  FsDocumentClass,
  FsItem,
  FsItemReference,
  FsNote,
  FsNoteReference,
  FsStructure,
  NoiseSpan,
} from "@doculink/shared";

const EMPTY_APPARATUS: FsApparatus = {
  notes: { searched: false, found: 0 },
  items: { searched: false, found: 0 },
  contents: { searched: false, found: 0 },
  parts: { searched: false, found: 0 },
};

/**
 * The two artifacts one cache build publishes, held per PDF.
 *
 * They arrive together and are read together: a citation's geometry lives in
 * the values model and what it resolves to lives in the structure model, joined
 * by span id. A document outside the financial tier carries no structure
 * artifact at all, which is not an error — `apparatus` is what says whether an
 * apparatus was looked for.
 */
export class ValuesCache {
  private readonly _values = new Map<string, Map<number, DetectedValue[]>>();
  private readonly _logicalValues = new Map<string, Map<number, DetectedValue[]>>();
  private readonly _references = new Map<string, Map<number, DetectedReference[]>>();
  private readonly _noise = new Map<string, Map<number, NoiseSpan[]>>();
  private readonly _structure = new Map<string, FsStructure | null>();
  private readonly _valuesDecoder: ((base64: string) => Promise<DocumentValues>) | undefined;
  private readonly _structureDecoder: ((base64: string) => Promise<FsStructure>) | undefined;

  constructor(
    valuesDecoder?: (base64: string) => Promise<DocumentValues>,
    structureDecoder?: (base64: string) => Promise<FsStructure>,
  ) {
    this._valuesDecoder = valuesDecoder;
    this._structureDecoder = structureDecoder;
  }

  async build(pdfId: string, documentValuesBase64?: string, fsStructureBase64?: string): Promise<void> {
    this.clearPdf(pdfId);
    this._reset(pdfId);
    if (documentValuesBase64) {
      try {
        const decode = this._valuesDecoder ?? (await import("@doculink/shared")).decodeDocumentValues;
        this._ingestValues(pdfId, await decode(documentValuesBase64));
      } catch {
        // A malformed optional artifact must not prevent the PDF itself loading.
        this._reset(pdfId);
      }
    }
    if (fsStructureBase64) {
      try {
        const decode = this._structureDecoder ?? (await import("@doculink/shared")).decodeFsStructure;
        this._structure.set(pdfId, await decode(fsStructureBase64));
      } catch {
        this._structure.set(pdfId, null);
      }
    }
  }

  private _reset(pdfId: string): void {
    this._values.set(pdfId, new Map());
    this._logicalValues.set(pdfId, new Map());
    this._references.set(pdfId, new Map());
    this._noise.set(pdfId, new Map());
    this._structure.set(pdfId, null);
  }

  private _ingestValues(pdfId: string, model: DocumentValues): void {
    const byPage = new Map<number, DetectedValue[]>();
    const logicalValues = new Map<number, DetectedValue[]>();
    const references = new Map<number, DetectedReference[]>();
    const noise = new Map<number, NoiseSpan[]>();
    for (const page of model.pages) {
      // Tolerated rather than required: a decoder seam may omit an empty array.
      const pageNoise = page.noise ?? [];
      if (pageNoise.length > 0) noise.set(page.pageIndex, pageNoise);
      for (const value of page.values) {
        const pageLogicalValues = logicalValues.get(page.pageIndex) ?? [];
        pageLogicalValues.push(value);
        logicalValues.set(page.pageIndex, pageLogicalValues);
        const segments = value.segments ?? [{ pageIndex: page.pageIndex, text: value.text, bounds: value.bounds }];
        for (const segment of segments) {
          const pageValues = byPage.get(segment.pageIndex) ?? [];
          pageValues.push({ ...value, bounds: segment.bounds });
          byPage.set(segment.pageIndex, pageValues);
        }
      }
      for (const reference of page.references ?? []) {
        const segments = reference.segments
          ?? [{ pageIndex: page.pageIndex, text: reference.text, bounds: reference.bounds }];
        for (const segment of segments) {
          const pageReferences = references.get(segment.pageIndex) ?? [];
          pageReferences.push({ ...reference, bounds: segment.bounds });
          references.set(segment.pageIndex, pageReferences);
        }
      }
    }
    this._values.set(pdfId, byPage);
    this._logicalValues.set(pdfId, logicalValues);
    this._references.set(pdfId, references);
    this._noise.set(pdfId, noise);
  }

  valuesOnPage(pdfId: string, pageIndex: number): DetectedValue[] {
    return this._values.get(pdfId)?.get(pageIndex) ?? [];
  }

  /** Spans that identify rather than measure. Click targets, exactly as values are. */
  referencesOnPage(pdfId: string, pageIndex: number): DetectedReference[] {
    return this._references.get(pdfId)?.get(pageIndex) ?? [];
  }

  /** Spans the detector refused on this page. Diagnostics — never click targets. */
  noiseOnPage(pdfId: string, pageIndex: number): NoiseSpan[] {
    return this._noise.get(pdfId)?.get(pageIndex) ?? [];
  }

  has(pdfId: string): boolean { return this._values.has(pdfId); }

  valueCount(pdfId: string): number {
    let total = 0;
    for (const values of this._logicalValues.get(pdfId)?.values() ?? []) total += values.length;
    return total;
  }

  referenceCount(pdfId: string): number {
    let total = 0;
    for (const entries of this._references.get(pdfId)?.values() ?? []) total += entries.length;
    return total;
  }

  noiseCount(pdfId: string): number {
    let total = 0;
    for (const entries of this._noise.get(pdfId)?.values() ?? []) total += entries.length;
    return total;
  }

  logicalValuesOnPage(pdfId: string, pageIndex: number): DetectedValue[] {
    return this._logicalValues.get(pdfId)?.get(pageIndex) ?? [];
  }

  /** What the document appears to be. "neither" also when no structure was published. */
  documentClass(pdfId: string): FsDocumentClass {
    return this._structure.get(pdfId)?.documentClass ?? "neither";
  }

  /**
   * What each apparatus was looked for and what it yielded. All zero and
   * unsearched when the document carries no structure artifact, which is what
   * distinguishes "not a financial document" from "a statement with no notes".
   */
  apparatus(pdfId: string): FsApparatus {
    return this._structure.get(pdfId)?.apparatus ?? EMPTY_APPARATUS;
  }

  /** Canonical financial-statement notes detected across the document. */
  notes(pdfId: string): FsNote[] {
    return this._structure.get(pdfId)?.notes ?? [];
  }

  /** Citations resolved to entries in the canonical note catalogue. */
  noteReferences(pdfId: string): FsNoteReference[] {
    return this._structure.get(pdfId)?.noteReferences ?? [];
  }

  /** Canonical filing items detected across the document. */
  items(pdfId: string): FsItem[] {
    return this._structure.get(pdfId)?.items ?? [];
  }

  /** Citations resolved to entries in the canonical item catalogue. */
  itemReferences(pdfId: string): FsItemReference[] {
    return this._structure.get(pdfId)?.itemReferences ?? [];
  }

  clearPdf(pdfId: string): void {
    this._values.delete(pdfId);
    this._logicalValues.delete(pdfId);
    this._references.delete(pdfId);
    this._noise.delete(pdfId);
    this._structure.delete(pdfId);
  }

  clear(): void {
    this._values.clear();
    this._logicalValues.clear();
    this._references.clear();
    this._noise.clear();
    this._structure.clear();
  }
}
