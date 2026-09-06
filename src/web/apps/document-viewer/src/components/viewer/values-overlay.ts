import type {
  DetectedReference,
  DetectedValue,
  FsHeading,
  FsItemTocEntry,
  HoverTipContent,
  NoiseSpan,
  SpanBounds,
} from "@doculink/shared";
import {
  HoverTip,
  describeItemHeader,
  describeItemReference,
  describeItemTocEntry,
  describeNoise,
  describeNoteHeader,
  describeNoteReference,
  describeReference,
  describeValue,
} from "@doculink/shared";
import type { ValuesCache } from "../../services/values-cache.js";
import type { PdfViewer } from "./pdf-viewer.js";
import { ensureOverlayLayer } from "./page-renderer.js";

const OVERLAY_CLASS = "values";
const REFERENCE_CLASS = "values-references";
const NOISE_CLASS = "values-noise";

/** A span the user can click to create a link: a value, or a reference. */
export type ClickableSpan =
  | { category: "value"; value: DetectedValue }
  | { category: "reference"; reference: DetectedReference };

function contains(outer: SpanBounds, inner: SpanBounds): boolean {
  return inner.x >= outer.x - 0.001
    && inner.y >= outer.y - 0.001
    && inner.x + inner.width <= outer.x + outer.width + 0.001
    && inner.y + inner.height <= outer.y + outer.height + 0.001;
}

export class ValuesOverlay {
  private _debugVisible = false;
  private _referencesVisible = false;
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
    const noise = this._noiseVisible ? this._cache.noiseOnPage(pdfId, pageIndex) : [];
    if (values.length === 0 && references.length === 0 && noise.length === 0) return;
    const layer = ensureOverlayLayer(wrapper);
    if (values.length > 0) {
      const container = document.createElement("div");
      container.className = this._debugVisible ? `${OVERLAY_CLASS} ${OVERLAY_CLASS}--debug` : OVERLAY_CLASS;
      for (const value of values) {
        container.appendChild(this._spanElement(pdfId, pageIndex, {
          id: value.id,
          kind: value.kind,
          text: value.text,
          bounds: value.bounds,
          className: `${OVERLAY_CLASS}__value ${OVERLAY_CLASS}__value--${value.kind}`,
          span: { category: "value", value },
        }));
      }
      layer.appendChild(container);
    }
    if (references.length > 0) {
      const container = document.createElement("div");
      container.className = this._referencesVisible
        ? `${REFERENCE_CLASS} ${REFERENCE_CLASS}--debug`
        : REFERENCE_CLASS;
      for (const reference of references) {
        container.appendChild(this._spanElement(pdfId, pageIndex, {
          id: reference.id,
          kind: reference.kind,
          text: reference.text,
          bounds: reference.bounds,
          className: `${REFERENCE_CLASS}__value ${REFERENCE_CLASS}__value--${reference.kind}`,
          span: { category: "reference", reference },
        }));
      }
      layer.appendChild(container);
    }
    if (noise.length > 0) {
      const container = document.createElement("div");
      container.className = NOISE_CLASS;
      for (const entry of noise) container.appendChild(ValuesOverlay._noiseElement(entry));
      layer.appendChild(container);
    }
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
    const debugging = options.span.category === "value" ? this._debugVisible : this._referencesVisible;
    if (!debugging) button.title = `Create link for ${options.kind}: ${options.text}`;
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
    this._tip.setEnabled(this._debugVisible || this._referencesVisible || this._noiseVisible);
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
          consider(value.bounds, () => describeValue(value));
        }
      }
      if (this._referencesVisible) {
        for (const reference of this._cache.referencesOnPage(pdfId, pageIndex)) {
          consider(reference.bounds, () => this._describeReference(pdfId, reference));
        }
      }
      if (this._noiseVisible) {
        for (const entry of this._cache.noiseOnPage(pdfId, pageIndex)) {
          consider(entry.bounds, () => this._describeNoise(pdfId, pageIndex, entry));
        }
      }
      return best;
    }
    return null;
  }

  /**
   * A citation is published in both artifacts: the span carries its geometry,
   * the catalogue carries what it resolves to, and the span's own id is the
   * join. No matching by text or position is needed, which is the point of
   * content-addressed ids.
   */
  private _describeReference(pdfId: string, reference: DetectedReference): HoverTipContent {
    if (reference.kind === "note") {
      const resolved = this._cache.noteReferences(pdfId).find((entry) => entry.spanId === reference.id);
      if (resolved) return describeNoteReference(reference, resolved);
    }
    if (reference.kind === "item") {
      const resolved = this._cache.itemReferences(pdfId).find((entry) => entry.spanId === reference.id);
      if (resolved) return describeItemReference(reference, resolved);
    }
    return describeReference(reference);
  }

  /**
   * A heading and a contents row are structure and live in the catalogue. What
   * the value tier publishes is the number printed inside one, so the entry
   * behind a hovered refusal is the catalogue occurrence whose bounds enclose
   * it on the same page.
   */
  private _describeNoise(
    pdfId: string,
    pageIndex: number,
    entry: NoiseSpan,
  ): HoverTipContent {
    const encloses = (candidate: { pageIndex: number; bounds: SpanBounds }): boolean => (
      candidate.pageIndex === pageIndex && contains(candidate.bounds, entry.bounds)
    );
    if (entry.reason === "note-header") {
      for (const note of this._cache.notes(pdfId)) {
        const header = note.headers.find((candidate: FsHeading) => encloses(candidate));
        if (header) return describeNoteHeader(entry, note, header);
      }
    }
    if (entry.reason === "item-header") {
      for (const item of this._cache.items(pdfId)) {
        const header = item.headers.find((candidate: FsHeading) => encloses(candidate));
        if (header) return describeItemHeader(entry, item, header);
      }
    }
    if (entry.reason === "item-toc-entry") {
      for (const item of this._cache.items(pdfId)) {
        const tocEntry = item.tocEntries.find((candidate: FsItemTocEntry) => encloses(candidate));
        if (tocEntry) return describeItemTocEntry(entry, item, tocEntry);
      }
    }
    return describeNoise(entry);
  }

  private _clearPage(wrapper: HTMLDivElement): void {
    for (const element of Array.from(
      wrapper.querySelectorAll(`.${OVERLAY_CLASS}, .${REFERENCE_CLASS}, .${NOISE_CLASS}`),
    )) element.remove();
  }
}
