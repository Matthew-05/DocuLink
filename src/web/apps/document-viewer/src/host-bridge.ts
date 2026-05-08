import type { PdfEntry, LinkRectPayload, LinkedRectEntry, NormalizedRect } from "./types/index.js";

interface PdfPayload {
  id: string;
  name: string;
  base64: string;
}

interface PdfsLoadedMessage {
  type: "pdfs-loaded";
  pdfs: PdfPayload[];
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

/** Revoke previously created object URLs to avoid memory leaks. */
let _activeObjectUrls: string[] = [];

function revokeActiveUrls(): void {
  for (const url of _activeObjectUrls) {
    URL.revokeObjectURL(url);
  }
  _activeObjectUrls = [];
}

function base64ToObjectUrl(base64: string): string {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  const blob = new Blob([bytes], { type: "application/pdf" });
  const url = URL.createObjectURL(blob);
  _activeObjectUrls.push(url);
  return url;
}

function handleMessage(
  raw: unknown,
  onEntries: (entries: PdfEntry[]) => void,
  onLinkedRectangles?: (rects: LinkedRectEntry[]) => void,
  onNavigateToRectangle?: (id: string, pdfId: string, page: number) => void,
  onClearRectangleHighlight?: () => void,
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

      revokeActiveUrls();
      const entries: PdfEntry[] = msg.pdfs.map((pdf) => ({
        id:  pdf.id,
        name: pdf.name || pdf.id,
        url:  base64ToObjectUrl(pdf.base64),
      }));
      onEntries(entries);
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

    if (type === "clear-rectangle-highlight") {
      onClearRectangleHighlight?.();
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
    handleMessage(data, onEntries, onLinkedRectangles, onNavigateToRectangle, onClearRectangleHighlight);
  });

  postToHost({ type: "viewer-ready" });
}

export function sendLinkRectangleClicked(id: string): void {
  postToHost({ type: "link-rectangle-clicked", id });
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

export function sendCacheBuildStarted(): void {
  postToHost({ type: "cache-build-started" });
}

export function sendCacheBuildComplete(): void {
  postToHost({ type: "cache-build-complete" });
}
