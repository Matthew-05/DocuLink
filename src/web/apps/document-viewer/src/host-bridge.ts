import type { PdfEntry } from "./types/index.js";

interface PdfPayload {
  id: string;
  name: string;
  base64: string;
}

interface PdfsLoadedMessage {
  type: "pdfs-loaded";
  pdfs: PdfPayload[];
}

type HostMessage = PdfsLoadedMessage;

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
  onEntries: (entries: PdfEntry[]) => void
): void {
  let msg: HostMessage;

  try {
    const parsed: unknown =
      typeof raw === "string" ? (JSON.parse(raw) as unknown) : raw;

    if (
      typeof parsed !== "object" ||
      parsed === null ||
      (parsed as { type?: unknown }).type !== "pdfs-loaded"
    ) {
      return;
    }

    msg = parsed as PdfsLoadedMessage;
  } catch {
    return;
  }

  revokeActiveUrls();

  const entries: PdfEntry[] = msg.pdfs.map((pdf) => ({
    id: pdf.id,
    name: pdf.name || pdf.id,
    url: base64ToObjectUrl(pdf.base64),
  }));

  onEntries(entries);
}

interface WebView2Bridge extends EventTarget {
  postMessage(message: string): void;
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
export function initHostBridge(onEntries: (entries: PdfEntry[]) => void): void {
  const webview = (
    window as unknown as { chrome?: { webview?: WebView2Bridge } }
  ).chrome?.webview;

  if (!webview) {
    return;
  }

  webview.addEventListener("message", (event: Event) => {
    const data = (event as MessageEvent<unknown>).data;
    handleMessage(data, onEntries);
  });

  webview.postMessage(JSON.stringify({ type: "viewer-ready" }));
}
