import type {
  FolderInfo,
  KeyColumnInfo,
  LinkCreationRequest,
  MatcherDataLoadedPayload,
  MatcherPdf,
  MatcherReadyPayload,
  MatcherRow,
  OutputColumnInfo,
  SelectionInfo,
} from "./types/index.js";

type InboundMessage =
  | { type: "matcher-ready"; rangeDisplay: string; rowCount: number; keyColumns: KeyColumnInfo[]; outputColumns: OutputColumnInfo[]; folders: FolderInfo[] }
  | { type: "matcher-selection-changed"; rangeDisplay: string; rowCount: number; keyColumns: KeyColumnInfo[]; outputColumns: OutputColumnInfo[] }
  | { type: "matcher-data-loaded"; rows: MatcherRow[]; pdfs: MatcherPdf[] }
  | { type: "links-created"; results: Array<{ rowIndex: number; outputColNumber: number; success: boolean }> };

function send(msg: object): void {
  (window as { chrome?: { webview?: { postMessage?: (s: string) => void } } }).chrome?.webview?.postMessage?.(
    JSON.stringify(msg),
  );
}

export interface HostBridgeCallbacks {
  onMatcherReady: (payload: MatcherReadyPayload) => void;
  onSelectionChanged: (info: SelectionInfo) => void;
  onMatcherDataLoaded: (payload: MatcherDataLoadedPayload) => void;
  onLinksCreated: (results: Array<{ rowIndex: number; outputColNumber: number; success: boolean }>) => void;
}

export function initHostBridge(callbacks: HostBridgeCallbacks): void {
  const webview = (window as { chrome?: { webview?: { addEventListener?: (e: string, h: (ev: Event) => void) => void } } }).chrome?.webview;
  if (!webview?.addEventListener) return;

  webview.addEventListener("message", (event: Event) => {
    const msg = JSON.parse((event as MessageEvent<string>).data) as InboundMessage;
    switch (msg.type) {
      case "matcher-ready":
        callbacks.onMatcherReady({
          rangeDisplay: msg.rangeDisplay,
          rowCount: msg.rowCount,
          keyColumns: msg.keyColumns,
          outputColumns: msg.outputColumns,
          folders: msg.folders,
        });
        break;
      case "matcher-selection-changed":
        callbacks.onSelectionChanged({
          rangeDisplay: msg.rangeDisplay,
          rowCount: msg.rowCount,
          keyColumns: msg.keyColumns,
          outputColumns: msg.outputColumns,
        });
        break;
      case "matcher-data-loaded":
        callbacks.onMatcherDataLoaded({ rows: msg.rows, pdfs: msg.pdfs });
        break;
      case "links-created":
        callbacks.onLinksCreated(msg.results);
        break;
    }
  });

  send({ type: "matcher-app-ready" });
}

export function sendStartMatching(outputColNumbers: number[], folderIds: string[]): void {
  send({ type: "start-matching", outputColNumbers, folderIds });
}

export function sendCreateLinks(links: LinkCreationRequest[]): void {
  send({ type: "create-links", links });
}
