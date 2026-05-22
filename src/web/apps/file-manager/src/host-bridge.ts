import type { FileEntry, FolderEntry } from "./types/index.js";

// ── Inbound (host → web) ──────────────────────────────────────────────────────

interface FilesLoadedMessage {
  type: "files-loaded";
  folders: FolderEntry[];
  files: FileEntry[];
}

interface OcrStatusMessage {
  type: "ocr-status";
  pdfId: string;
  status: "queued" | "processing" | "complete" | "error";
  message?: string;
}

interface ResetUiMessage {
  type: "reset-ui";
}

type HostMessage = FilesLoadedMessage | OcrStatusMessage | ResetUiMessage;

// ── Outbound (web → host) ─────────────────────────────────────────────────────

export interface AddFilePayload {
  name: string;
  base64: string;
  folderId?: string;
}

interface WebView2Bridge extends EventTarget {
  postMessage(message: string): void;
}

function getWebView(): WebView2Bridge | null {
  return (
    (window as unknown as { chrome?: { webview?: WebView2Bridge } }).chrome
      ?.webview ?? null
  );
}

function send(msg: object): void {
  getWebView()?.postMessage(JSON.stringify(msg));
}

let _onResetUi: (() => void) | null = null;

// ── Public API ────────────────────────────────────────────────────────────────

export function registerUiResetHandler(handler: () => void): void {
  _onResetUi = handler;
}

export function initHostBridge(
  onFilesLoaded: (folders: FolderEntry[], files: FileEntry[]) => void,
  onOcrStatus?: (pdfId: string, status: OcrStatusMessage["status"], message: string | undefined) => void
): void {
  const webview = getWebView();
  if (!webview) return;

  webview.addEventListener("message", (event: Event) => {
    const raw = (event as MessageEvent<unknown>).data;
    let msg: HostMessage;
    try {
      const parsed: unknown =
        typeof raw === "string" ? (JSON.parse(raw) as unknown) : raw;
      if (typeof parsed !== "object" || parsed === null) return;
      msg = parsed as HostMessage;
    } catch {
      return;
    }

    if (msg.type === "files-loaded") {
      onFilesLoaded(msg.folders, msg.files);
    } else if (msg.type === "ocr-status" && onOcrStatus) {
      onOcrStatus(msg.pdfId, msg.status, msg.message);
    } else if (msg.type === "reset-ui") {
      _onResetUi?.();
    }
  });

  send({ type: "manager-ready" });
}

export function sendAddFiles(files: AddFilePayload[]): void {
  send({ type: "add-files", files });
}

export function sendRenameFile(id: string, newName: string): void {
  send({ type: "rename-file", id, newName });
}

export function sendRemoveFile(id: string): void {
  send({ type: "remove-file", id });
}

export function sendMoveFile(id: string, folderId: string | null): void {
  const msg: Record<string, unknown> = { type: "move-file", id };
  if (folderId) msg["folderId"] = folderId;
  send(msg);
}

export function sendAddFolder(name: string): void {
  send({ type: "add-folder", name });
}

export function sendRenameFolder(id: string, newName: string): void {
  send({ type: "rename-folder", id, newName });
}

export function sendRemoveFolder(id: string): void {
  send({ type: "remove-folder", id });
}

export function sendOcrPdfs(pdfIds: string[]): void {
  send({ type: "ocr-pdfs", pdfIds });
}

/** Notifies host of the actively selected folder (for OS drag-drop import). Omit folderId → All Files. */
export function sendSelectedFolder(folderId: string | null): void {
  const msg: Record<string, unknown> = { type: "set-selected-folder" };
  if (folderId) msg["folderId"] = folderId;
  send(msg);
}
