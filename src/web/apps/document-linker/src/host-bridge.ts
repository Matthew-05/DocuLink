import type {
  FolderInfo,
  KeyColumnInfo,
  LinkCreationRequest,
  LinkerDataLoadedPayload,
  LinkerPdf,
  LinkerReadyPayload,
  LinkerRow,
  OutputColumnInfo,
  SelectionInfo,
} from "./types/index.js";

type InboundMessage =
  | { type: "linker-ready"; rowCount: number; keyColumns: KeyColumnInfo[]; outputColumns: OutputColumnInfo[]; folders: FolderInfo[] }
  | { type: "linker-selection-changed"; rowCount: number; keyColumns: KeyColumnInfo[]; outputColumns: OutputColumnInfo[] }
  | { type: "linker-data-loaded"; rows: LinkerRow[]; pdfs: LinkerPdf[] }
  | { type: "links-created"; results: Array<{ rowIndex: number; outputColNumber: number; success: boolean }> }
  | { type: "linker-confirm-overwrite-result"; confirmed: boolean };

let _confirmOverwriteResolve: ((confirmed: boolean) => void) | null = null;

function send(msg: object): void {
  (window as { chrome?: { webview?: { postMessage?: (s: string) => void } } }).chrome?.webview?.postMessage?.(
    JSON.stringify(msg),
  );
}

export interface HostBridgeCallbacks {
  onLinkerReady: (payload: LinkerReadyPayload) => void;
  onSelectionChanged: (info: SelectionInfo) => void;
  onLinkerDataLoaded: (payload: LinkerDataLoadedPayload) => void;
  onLinksCreated: (results: Array<{ rowIndex: number; outputColNumber: number; success: boolean }>) => void;
}

export function initHostBridge(callbacks: HostBridgeCallbacks): void {
  const webview = (window as { chrome?: { webview?: { addEventListener?: (e: string, h: (ev: Event) => void) => void } } }).chrome?.webview;
  if (!webview?.addEventListener) return;

  webview.addEventListener("message", (event: Event) => {
    const msg = JSON.parse((event as MessageEvent<string>).data) as InboundMessage;
    switch (msg.type) {
      case "linker-ready":
        callbacks.onLinkerReady({
          rowCount: msg.rowCount,
          keyColumns: msg.keyColumns,
          outputColumns: msg.outputColumns,
          folders: msg.folders,
        });
        break;
      case "linker-selection-changed":
        callbacks.onSelectionChanged({
          rowCount: msg.rowCount,
          keyColumns: msg.keyColumns,
          outputColumns: msg.outputColumns,
        });
        break;
      case "linker-data-loaded":
        callbacks.onLinkerDataLoaded({ rows: msg.rows, pdfs: msg.pdfs });
        break;
      case "links-created":
        callbacks.onLinksCreated(msg.results);
        break;
      case "linker-confirm-overwrite-result": {
        const resolve = _confirmOverwriteResolve;
        _confirmOverwriteResolve = null;
        resolve?.(msg.confirmed);
        break;
      }
    }
  });

  send({ type: "linker-app-ready" });
}

export function sendStartMatching(outputColNumbers: number[], folderIds: string[]): void {
  send({ type: "start-matching", outputColNumbers, folderIds });
}

export function sendSelectionLocked(): void {
  send({ type: "linker-selection-locked" });
}

export function sendSelectionUnlocked(): void {
  send({ type: "linker-selection-unlocked" });
}

/** Asks the host to select the range that would receive links for `colNumber`. */
export function sendPreviewOutputRange(colNumber: number): void {
  send({ type: "linker-preview-output-range", colNumber });
}

/** Asks the host to restore the selection captured before hover preview began. */
export function sendClearOutputRangePreview(): void {
  send({ type: "linker-clear-output-range-preview" });
}

export function sendLinkerLog(message: string): void {
  send({ type: "linker-log", message });
}

export function sendLinkerGeometryPrepared(pdfId: string, geometryBase64: string): void {
  send({ type: "linker-geometry-prepared", pdfId, geometryBase64 });
}

export function sendCreateLinks(links: LinkCreationRequest[]): void {
  send({ type: "create-links", links });
}

export function sendClose(): void {
  send({ type: "linker-close" });
}

export function sendCheckOutputContent(colNumbers: number[]): Promise<boolean> {
  return new Promise((resolve) => {
    _confirmOverwriteResolve = resolve;
    send({ type: "check-output-content", colNumbers });
  });
}
