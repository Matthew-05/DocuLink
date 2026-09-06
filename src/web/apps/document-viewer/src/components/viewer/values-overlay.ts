import type {
  DetectedReference,
  DetectedStructure,
  DetectedValue,
  FinancialHeading,
  FinancialItemTocEntry,
  HoverTipContent,
  NoiseSpan,
  SpanBounds,
  SpanTipContext,
} from "@talliark/shared";
import {
  HoverTip,
  describeItemHeader,
  describeItemReference,
  describeItemTocEntry,
  describeNoise,
  describeNoteHeader,
  describeNoteReference,
  describeReference,
  describeStructure,
  describeValue,
} from "@talliark/shared";
import type { ClickableSpan } from "./span-link-bounds.js";
import type { ValuesCache } from "../../services/values-cache.js";
import type { PdfViewer } from "./pdf-viewer.js";
import { ensureOverlayLayer } from "./page-renderer.js";

const OVERLAY_CLASS = "values";
const REFERENCE_CLASS = "values-references";
const STRUCTURE_CLASS = "values-structure";
const NOISE_CLASS = "values-noise";

function contains(outer: SpanBounds, inner: SpanBounds): boolean {
  return inner.x >= outer.x - 0.001
    && inner.y >= outer.y - 0.001
    && inner.x + inner.width <= outer.x + outer.width + 0.001
    && inner.y + inner.height <= outer.y + outer.height + 0.001;
}

export class ValuesOverlay {
  private _debugVisible = false;
  private _referencesVisible = false;
  private _structureVisible = false;
  private _noiseVisible = false;
  private readonly _tip: HoverTip;
  private readonly _callbacks: Array<(pdfId: string, pageIndex: number, span: ClickableSpan) => void> = [];
  private readonly _viewer: PdfViewer;
  private readonly _cache: ValuesCache;

  constructor(viewer: PdfViewer, cache: ValuesCache) {
    this._viewer = viewer;
    this._cache = cache;
    this._viewer.onDocumentChanged(() => this.refresh());
    // No layer can report its own hover: noise takes no pointer events at all,
    // and a click target's own tooltip would fight this one. The tip hit-tests
    // the cursor instead, and only while a debug view asks for it.
    this._tip = new HoverTip({
      className: "values__tip",
      resolve: (clientX, clientY) => this._describeAt(clientX, clientY),
    });
  }

  onSpanClicked(callback: (pdfId: string, pageIndex: number, span: ClickableSpan) => void): void {
    this._callbacks.push(callback);
  }

  toggle(): boolean { this._debugVisible ? this.hide() : this.show(); return this._debugVisible; }

  /** Show every value hit box. Click targets themselves are always active. */
  show(): void { this._debugVisible = true; this._syncTip(); this._renderAll(); }

  /** Hide diagnostic boxes while retaining transparent clickable targets. */
  hide(): void { this._debugVisible = false; this._syncTip(); this._renderAll(); }

  toggleReferences(): boolean {
    this._referencesVisible ? this.hideReferences() : this.showReferences();
    return this._referencesVisible;
  }

  /**
   * Draw the reference layer. References are click targets whether or not this
   * is on, exactly as values are — the flag decides only whether their boxes
   * are visible, and it means the same thing on every document.
   */
  showReferences(): void { this._referencesVisible = true; this._syncTip(); this._renderAll(); }

  hideReferences(): void { this._referencesVisible = false; this._syncTip(); this._renderAll(); }

  toggleStructure(): boolean {
    this._structureVisible ? this.hideStructure() : this.showStructure();
    return this._structureVisible;
  }

  /**
   * Draw the structure layer: the document indexing itself. Clickability is the
   * span's own business — today no structure span is a click target, and if one
   * becomes one this flag still decides only whether its box is drawn.
   */
  showStructure(): void { this._structureVisible = true; this._syncTip(); this._renderAll(); }

  hideStructure(): void { this._structureVisible = false; this._syncTip(); this._renderAll(); }

  toggleNoise(): boolean { this._noiseVisible ? this.hideNoise() : this.showNoise(); return this._noiseVisible; }

  /**
   * Show spans refused as neither value nor reference, each labelled with its
   * reason. Diagnostics only: noise draws nothing clickable, so it can be left
   * on while linking.
   */
  showNoise(): void { this._noiseVisible = true; this._syncTip(); this._renderAll(); }

  hideNoise(): void { this._noiseVisible = false; this._syncTip(); this._renderAll(); }

  refresh(): void { this._renderAll(); }

  private _renderAll(): void {
    // Zoom, re-render and page changes all move boxes out from under a held
    // cursor without a pointer event of any kind.
    this._tip.refresh();
    const pdfId = this._viewer.getActivePdfId();
    if (!pdfId) return;
    for (const { pageNumber, wrapper } of this._viewer.getPageLayout()) {
      this._renderPage(wrapper, pdfId, pageNumber - 1);
    }
  }

  private _renderPage(wrapper: HTMLDivElement, pdfId: string, pageIndex: number): void {
    this._clearPage(wrapper);
    const values = this._cache.valuesOnPage(pdfId, pageIndex);
    const references = this._cache.referencesOnPage(pdfId, pageIndex);
    const structure = this._cache.structureOnPage(pdfId, pageIndex);
    const noise = this._noiseVisible ? this._cache.noiseOnPage(pdfId, pageIndex) : [];
    if (
      values.length === 0 && references.length === 0
      && structure.length === 0 && noise.length === 0
    ) return;
    const layer = ensureOverlayLayer(wrapper);

    this._appendLayer(layer, pdfId, pageIndex, {
      spans: values,
      base: OVERLAY_CLASS,
      debug: this._debugVisible,
      toSpan: (value) => ({ category: "value", value }),
    });
    this._appendLayer(layer, pdfId, pageIndex, {
      spans: references,
      base: REFERENCE_CLASS,
      debug: this._referencesVisible,
      toSpan: (reference) => ({ category: "reference", reference }),
    });
    this._appendLayer(layer, pdfId, pageIndex, {
      spans: structure,
      base: STRUCTURE_CLASS,
      debug: this._structureVisible,
      toSpan: (span) => ({ category: "structure", structure: span }),
    });

    if (noise.length > 0) {
      const container = document.createElement("div");
      container.className = NOISE_CLASS;
      for (const entry of noise) container.appendChild(ValuesOverlay._noiseElement(entry));
      layer.appendChild(container);
    }
  }

  /**
   * One category's layer. Whether a span becomes a button is read from the span
   * itself, never from which layer it is in: `clickable` is the contract's own
   * statement of it, and re-deriving it here is what the field exists to stop.
   * A layer with nothing clickable in it is drawn inert, so a rectangle drag
   * begun over it still reaches the page.
   */
  private _appendLayer<T extends { id: string; kind: string; text: string; bounds: SpanBounds; clickable: boolean }>(
    layer: HTMLElement,
    pdfId: string,
    pageIndex: number,
    options: { spans: T[]; base: string; debug: boolean; toSpan: (span: T) => ClickableSpan },
  ): void {
    if (options.spans.length === 0) return;
    const container = document.createElement("div");
    container.className = options.debug ? `${options.base} ${options.base}--debug` : options.base;
    for (const span of options.spans) {
      container.appendChild(
        span.clickable
          ? this._spanElement(pdfId, pageIndex, {
            id: span.id,
            kind: span.kind,
            text: span.text,
            bounds: span.bounds,
            className: `${options.base}__value ${options.base}__value--${span.kind}`,
            debug: options.debug,
            span: options.toSpan(span),
          })
          : ValuesOverlay._inertElement(span, `${options.base}__value ${options.base}__value--${span.kind}`),
      );
    }
    layer.appendChild(container);
  }

  /** A drawn but uncapturable span: no focus, no pointer events, no click. */
  private static _inertElement(
    span: { kind: string; bounds: SpanBounds },
    className: string,
  ): HTMLDivElement {
    const element = document.createElement("div");
    element.className = `${className} ${className.split(" ")[0]}--inert`;
    element.style.left = `${span.bounds.x * 100}%`;
    element.style.top = `${span.bounds.y * 100}%`;
    element.style.width = `${span.bounds.width * 100}%`;
    element.style.height = `${span.bounds.height * 100}%`;
    element.setAttribute("aria-hidden", "true");
    return element;
  }

  /** An inert marker. Noise is never focusable and never takes a pointer event. */
  private static _noiseElement(entry: NoiseSpan): HTMLDivElement {
    const element = document.createElement("div");
    element.className = `${NOISE_CLASS}__value ${NOISE_CLASS}__value--${entry.kind}`;
    element.style.left = `${entry.bounds.x * 100}%`;
    element.style.top = `${entry.bounds.y * 100}%`;
    element.style.width = `${entry.bounds.width * 100}%`;
    element.style.height = `${entry.bounds.height * 100}%`;
    element.dataset["noiseReason"] = entry.reason;
    element.setAttribute("aria-hidden", "true");
    return element;
  }

  private _spanElement(
    pdfId: string,
    pageIndex: number,
    options: {
      id: string;
      kind: string;
      text: string;
      bounds: SpanBounds;
      className: string;
      debug: boolean;
      span: ClickableSpan;
    },
  ): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = options.className;
    button.style.left = `${options.bounds.x * 100}%`;
    button.style.top = `${options.bounds.y * 100}%`;
    button.style.width = `${options.bounds.width * 100}%`;
    button.style.height = `${options.bounds.height * 100}%`;
    // The debug view explains this box in the hover tip; two tooltips would
    // race each other, so the native one only stands in when it is off.
    if (!options.debug) button.title = `Create link for ${options.kind}: ${options.text}`;
    button.dataset["spanId"] = options.id;
    button.setAttribute("aria-label", `Create link for detected ${options.kind} ${options.text}`);
    button.addEventListener("pointerdown", (event) => event.stopPropagation());
    button.addEventListener("mousedown", (event) => {
      event.preventDefault();
      event.stopPropagation();
    });
    const setRelatedHover = (active: boolean): void => {
      for (const related of Array.from(
        this._viewer.element.querySelectorAll<HTMLElement>("[data-span-id]"),
      ).filter((candidate) => candidate.dataset["spanId"] === options.id)) {
        related.classList.toggle(`${OVERLAY_CLASS}__value--related-hover`, active);
      }
    };
    button.addEventListener("pointerenter", () => setRelatedHover(true));
    button.addEventListener("pointerleave", () => setRelatedHover(false));
    button.addEventListener("focus", () => setRelatedHover(true));
    button.addEventListener("blur", () => setRelatedHover(false));
    button.addEventListener("click", (event) => {
      event.preventDefault(); event.stopPropagation();
      if (button.disabled) return;
      button.disabled = true;
      globalThis.setTimeout(() => { button.disabled = false; }, 1_500);
      for (const callback of this._callbacks) callback(pdfId, pageIndex, options.span);
    });
    return button;
  }

  /** The tip is a debug aid, so it opens only while a debug view is on. */
  private _syncTip(): void {
    this._tip.setEnabled(
      this._debugVisible || this._referencesVisible
      || this._structureVisible || this._noiseVisible,
    );
  }

  /**
   * What sits under a client point: the smallest box across whichever layers
   * are showing, so a footnote marker inside a wider span stays reachable.
   */
  private _describeAt(clientX: number, clientY: number): HoverTipContent | null {
    const pdfId = this._viewer.getActivePdfId();
    if (!pdfId) return null;
    for (const { pageNumber, wrapper } of this._viewer.getPageLayout()) {
      const rect = wrapper.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) continue;
      if (clientX < rect.left || clientX > rect.right) continue;
      if (clientY < rect.top || clientY > rect.bottom) continue;
      const x = (clientX - rect.left) / rect.width;
      const y = (clientY - rect.top) / rect.height;
      const pageIndex = pageNumber - 1;
      const context = this._tipContext(pdfId, pageIndex);
      let best: HoverTipContent | null = null;
      let bestArea = Infinity;
      const consider = (bounds: SpanBounds, describe: () => HoverTipContent): void => {
        if (x < bounds.x || x > bounds.x + bounds.width) return;
        if (y < bounds.y || y > bounds.y + bounds.height) return;
        const area = bounds.width * bounds.height;
        if (area >= bestArea) return;
        bestArea = area;
        best = describe();
      };
      if (this._debugVisible) {
        for (const value of this._cache.valuesOnPage(pdfId, pageIndex)) {
          consider(value.bounds, () => describeValue(value, context));
        }
      }
      if (this._referencesVisible) {
        for (const reference of this._cache.referencesOnPage(pdfId, pageIndex)) {
          consider(reference.bounds, () => this._describeReference(pdfId, reference, context));
        }
      }
      if (this._structureVisible) {
        for (const span of this._cache.structureOnPage(pdfId, pageIndex)) {
          consider(span.bounds, () => this._describeStructure(pdfId, pageIndex, span, context));
        }
      }
      if (this._noiseVisible) {
        for (const entry of this._cache.noiseOnPage(pdfId, pageIndex)) {
          consider(entry.bounds, () => describeNoise(entry, context));
        }
      }
      return best;
    }
    return null;
  }

  /** What a span's page and document say their figures are denominated in. */
  private _tipContext(pdfId: string, pageIndex: number): SpanTipContext {
    return {
      pageIndex,
      pageContext: this._cache.pageContext(pdfId, pageIndex),
      documentContext: this._cache.documentContext(pdfId),
    };
  }

  /**
   * A citation is published in both artifacts: the span carries its geometry,
   * the catalogue carries what it resolves to, and the span's own id is the
   * join. No matching by text or position is needed, which is the point of
   * content-addressed ids.
   */
  private _describeReference(
    pdfId: string,
    reference: DetectedReference,
    context: SpanTipContext,
  ): HoverTipContent {
    if (reference.kind === "note") {
      const resolved = this._cache.noteReferences(pdfId).find((entry) => entry.spanId === reference.id);
      if (resolved) return describeNoteReference(reference, resolved, context);
    }
    if (reference.kind === "item") {
      const resolved = this._cache.itemReferences(pdfId).find((entry) => entry.spanId === reference.id);
      if (resolved) return describeItemReference(reference, resolved, context);
    }
    return describeReference(reference, context);
  }

  /**
   * A heading and a contents row are one printed thing and live in the
   * catalogue. What the value tier publishes is the number inside one, so the
   * entry behind a hovered structure span is the catalogue occurrence whose
   * bounds enclose it on the same page.
   */
  private _describeStructure(
    pdfId: string,
    pageIndex: number,
    span: DetectedStructure,
    context: SpanTipContext,
  ): HoverTipContent {
    const encloses = (candidate: { pageIndex: number; bounds: SpanBounds }): boolean => (
      candidate.pageIndex === pageIndex && contains(candidate.bounds, span.bounds)
    );
    if (span.kind === "note-header") {
      for (const note of this._cache.notes(pdfId)) {
        const header = note.headers.find((candidate: FinancialHeading) => encloses(candidate));
        if (header) return describeNoteHeader(span, note, header, context);
      }
    }
    if (span.kind === "item-header") {
      for (const item of this._cache.items(pdfId)) {
        const header = item.headers.find((candidate: FinancialHeading) => encloses(candidate));
        if (header) return describeItemHeader(span, item, header, context);
      }
    }
    if (span.kind === "item-toc-entry") {
      for (const item of this._cache.items(pdfId)) {
        const tocEntry = item.tocEntries.find((candidate: FinancialItemTocEntry) => encloses(candidate));
        if (tocEntry) return describeItemTocEntry(span, item, tocEntry, context);
      }
    }
    return describeStructure(span, context);
  }

  private _clearPage(wrapper: HTMLDivElement): void {
    for (const element of Array.from(
      wrapper.querySelectorAll(
        `.${OVERLAY_CLASS}, .${REFERENCE_CLASS}, .${STRUCTURE_CLASS}, .${NOISE_CLASS}`,
      ),
    )) element.remove();
  }
}
