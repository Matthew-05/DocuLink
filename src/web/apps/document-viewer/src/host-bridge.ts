import type { PdfEntry, LinkRectPayload, LinkRectUpdatedPayload, LinkedRectEntry, NormalizedRect } from "./types/index.js";

interface PdfPayload {
  id: string;
  name: string;
  base64: string;
  geometryBase64?: string;
}

interface PdfsLoadedMessage {
  type: "pdfs-loaded";
  pdfs: PdfPayload[];
}

interface PdfUpdatedMessage {
  type: "pdf-updated";
  pdf: PdfPayload;
}

interface PdfAddedMessage {
  type: "pdf-added";
  pdf: PdfPayload;
}

interface PdfNameUpdatedMessage {
  type: "pdf-name-updated";
  id: string;
  name: string;
}

interface PdfRemovedMessage {
  type: "pdf-removed";
  id: string;
}

interface LinkedRectPayload {
  id: string;
  pdfId: string;
  page: number;
  rect: { x: number; y: number; width: number; height: number };
}

interface LinkedRectanglesLoadedMessage {
  type: "linked-rectangles-loaded";
  rectangles: LinkedRectPayload[];
}

interface NavigateToRectangleMessage {
  type: "navigate-to-rectangle";
  id: string;
  pdfId: string;
  page: number;
}

interface LinkRectanglesRemovedMessage {
  type: "link-rectangles-removed";
  ids: string[];
}

interface HighlightRectangleMessage {
  type: "highlight-rectangle";
  id: string;
}

/** Tracks object URLs by PDF id so single-document updates can revoke safely. */
const _urlsByPdfId = new Map<string, string>();

function revokeAllUrls(): void {
  for (const url of _urlsByPdfId.values()) {
    URL.revokeObjectURL(url);
  }
  _urlsByPdfId.clear();
}

function revokePdfUrl(pdfId: string): void {
  const existing = _urlsByPdfId.get(pdfId);
  if (!existing) return;
  URL.revokeObjectURL(existing);
  _urlsByPdfId.delete(pdfId);
}

function base64ToObjectUrl(base64: string): string {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  const blob = new Blob([bytes], { type: "application/pdf" });
  return URL.createObjectURL(blob);
}

function toPdfEntry(pdf: PdfPayload): PdfEntry {
  revokePdfUrl(pdf.id);
  const url = base64ToObjectUrl(pdf.base64);
  _urlsByPdfId.set(pdf.id, url);
  return {
    id:   pdf.id,
    name: pdf.name || pdf.id,
    url,
    geometryBase64: pdf.geometryBase64,
  };
}

function handleMessage(
  raw: unknown,
  onEntries: (entries: PdfEntry[]) => void,
  onLinkedRectangles?: (rects: LinkedRectEntry[]) => void,
  onNavigateToRectangle?: (id: string, pdfId: string, page: number) => void,
  onClearRectangleHighlight?: () => void,
  onHighlightRectangle?: (id: string) => void,
  onPdfUpdated?: (entry: PdfEntry) => void,
  onLinkRectanglesRemoved?: (ids: string[]) => void,
  onPdfNameUpdated?: (id: string, name: string) => void,
  onPdfRemoved?: (id: string) => void,
): void {
  try {
    const parsed: unknown =
      typeof raw === "string" ? (JSON.parse(raw) as unknown) : raw;

    if (
      typeof parsed !== "object" ||
      parsed === null
    ) {
      return;
    }

    const type = (parsed as { type?: unknown }).type;

    if (type === "pdfs-loaded") {
      const msg = parsed as PdfsLoadedMessage;

      revokeAllUrls();
      const entries: PdfEntry[] = msg.pdfs.map((pdf) => toPdfEntry(pdf));
      onEntries(entries);
      return;
    }

    if (type === "pdf-updated") {
      if (!onPdfUpdated) return;
      const msg = parsed as PdfUpdatedMessage;
      onPdfUpdated(toPdfEntry(msg.pdf));
      return;
    }

    if (type === "linked-rectangles-loaded") {
      if (!onLinkedRectangles) return;
      const lrMsg = parsed as LinkedRectanglesLoadedMessage;
      const rects: LinkedRectEntry[] = lrMsg.rectangles.map((r) => ({
        id:    r.id,
        pdfId: r.pdfId,
        page:  r.page,
        rect:  r.rect as NormalizedRect,
      }));
      onLinkedRectangles(rects);
      return;
    }

    if (type === "navigate-to-rectangle") {
      if (!onNavigateToRectangle) return;
      const navMsg = parsed as NavigateToRectangleMessage;
      onNavigateToRectangle(navMsg.id, navMsg.pdfId, navMsg.page);
      return;
    }

    if (type === "highlight-rectangle") {
      if (!onHighlightRectangle) return;
      const msg = parsed as HighlightRectangleMessage;
      onHighlightRectangle(msg.id);
      return;
    }

    if (type === "clear-rectangle-highlight") {
      onClearRectangleHighlight?.();
      return;
    }

    if (type === "link-rectangles-removed") {
      if (!onLinkRectanglesRemoved) return;
      const removedMsg = parsed as LinkRectanglesRemovedMessage;
      onLinkRectanglesRemoved(removedMsg.ids);
      return;
    }

    if (type === "pdf-added") {
      if (!onPdfUpdated) return;
      const msg = parsed as PdfAddedMessage;
      onPdfUpdated(toPdfEntry(msg.pdf));
      return;
    }

    if (type === "pdf-name-updated") {
      const msg = parsed as PdfNameUpdatedMessage;
      onPdfNameUpdated?.(msg.id, msg.name);
      return;
    }

    if (type === "pdf-removed") {
      const msg = parsed as PdfRemovedMessage;
      revokePdfUrl(msg.id);
      onPdfRemoved?.(msg.id);
      return;
    }
  } catch {
    // Malformed JSON or unexpected shape — silently ignore.
  }
}

interface WebView2Bridge extends EventTarget {
  postMessage(message: string): void;
}

let _webview: WebView2Bridge | null = null;

function postToHost(message: object): void {
  _webview?.postMessage(JSON.stringify(message));
}

/**
 * Registers a listener for host messages from the WebView2 C# add-in.
 * The provided callback is invoked with a fresh set of PdfEntry objects
 * each time the host pushes an updated PDF list.
 *
 * Sends a `viewer-ready` handshake immediately after registering so the host
 * knows JavaScript is initialized and can safely post the initial PDF list.
 *
 * Safe to call in non-WebView2 environments — does nothing when
 * `window.chrome.webview` is absent.
 */
export function initHostBridge(
  onEntries: (entries: PdfEntry[]) => void,
  onLinkedRectangles?: (rects: LinkedRectEntry[]) => void,
  onNavigateToRectangle?: (id: string, pdfId: string, page: number) => void,
  onClearRectangleHighlight?: () => void,
  onHighlightRectangle?: (id: string) => void,
  onPdfUpdated?: (entry: PdfEntry) => void,
  onLinkRectanglesRemoved?: (ids: string[]) => void,
  onPdfNameUpdated?: (id: string, name: string) => void,
  onPdfRemoved?: (id: string) => void,
): void {
  const webview = (
    window as unknown as { chrome?: { webview?: WebView2Bridge } }
  ).chrome?.webview;

  if (!webview) {
    return;
  }

  _webview = webview;

  webview.addEventListener("message", (event: Event) => {
    const data = (event as MessageEvent<unknown>).data;
    handleMessage(
      data,
      onEntries,
      onLinkedRectangles,
      onNavigateToRectangle,
      onClearRectangleHighlight,
      onHighlightRectangle,
      onPdfUpdated,
      onLinkRectanglesRemoved,
      onPdfNameUpdated,
      onPdfRemoved,
    );
  });

  postToHost({ type: "viewer-ready" });
}

export function sendLinkRectangleClicked(id: string): void {
  postToHost({ type: "link-rectangle-clicked", id });
}

export function sendLinkRectangleDeleted(id: string): void {
  postToHost({ type: "link-rectangle-deleted", id });
}

export function sendLinkRectangleCreated(payload: LinkRectPayload): void {
  postToHost({
    type:  "link-rectangle-created",
    pdfId: payload.pdfId,
    page:  payload.page,
    rect:  payload.rect,
    text:  payload.text,
  });
}

export function sendLinkRectangleUpdated(payload: LinkRectUpdatedPayload): void {
  postToHost({
    type:  "link-rectangle-updated",
    id:    payload.id,
    pdfId: payload.pdfId,
    page:  payload.page,
    rect:  payload.rect,
    text:  payload.text,
  });
}

export function sendCacheBuildStarted(): void {
  postToHost({ type: "cache-build-started" });
}

export function sendCacheBuildComplete(): void {
  postToHost({ type: "cache-build-complete" });
}
