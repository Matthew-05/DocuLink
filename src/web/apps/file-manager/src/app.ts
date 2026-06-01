import type { FileEntry, FolderEntry } from "./types/index.js";
import { initHostBridge, sendSelectedFolder, sendRemoveFile, sendMoveFile, sendOcrPdfs, sendCancelOcr } from "./host-bridge.js";
import { FolderPanel } from "./components/folder-panel/folder-panel.js";
import { FileTable } from "./components/file-table/file-table.js";
import { TableToolbar } from "./components/table-toolbar/table-toolbar.js";
import { wireFileManagerUiReset } from "./reset-ui.js";

export function mountApp(root: HTMLElement): void {
  root.className = "file-manager";

  const leftCol = document.createElement("div");
  leftCol.className = "file-manager__sidebar";

  const rightCol = document.createElement("div");
  rightCol.className = "file-manager__content";

  root.appendChild(leftCol);
  root.appendChild(rightCol);

  let selectedFolderId: string | null = null;
  let currentFolders: FolderEntry[] = [];
  let selectedIds: string[] = [];

  const activeOcrStatuses = new Set(["queued", "processing"]);

  function computeToolbarState(): { selectedHasActiveOcr: boolean; anyOcrRunning: boolean } {
    const selectedHasActiveOcr = selectedIds.some((id) => {
      const f = currentFiles.find((f) => f.id === id);
      return f !== undefined && activeOcrStatuses.has(f.status);
    });
    const anyOcrRunning = currentFiles.some((f) => activeOcrStatuses.has(f.status));
    return { selectedHasActiveOcr, anyOcrRunning };
  }

  const toolbar = new TableToolbar(rightCol, {
    onRemoveSelected() {
      const ids = fileTable.getSelectedIds();
      for (const id of ids) sendRemoveFile(id);
    },
    onMoveSelected(folderId: string | null) {
      const ids = fileTable.getSelectedIds();
      for (const id of ids) sendMoveFile(id, folderId);
      fileTable.clearSelection();
    },
    onProcessSelected() {
      const ids = fileTable.getSelectedIds();
      if (ids.length > 0) {
        sendOcrPdfs(ids);
        fileTable.setSelectionLocked(true);
      }
    },
    onCancelOcr() {
      sendCancelOcr();
    },
    onFilterChange(text: string) {
      fileTable.setFilter(text);
    },
  });

  const fileTable = new FileTable(rightCol, {
    onSelectionChange(ids: string[]) {
      selectedIds = ids;
      const { selectedHasActiveOcr, anyOcrRunning } = computeToolbarState();
      toolbar.update(ids.length, selectedHasActiveOcr, anyOcrRunning);
    },
  });

  const folderPanel = new FolderPanel(leftCol, {
    onSelectionChange(folderId: string | null) {
      selectedFolderId = folderId;
      sendSelectedFolder(folderId);
      fileTable.update(currentFiles, selectedFolderId);
    },
  });

  // Spacer to reserve space for native C# dropzone panel at the bottom
  const dropzoneSpacer = document.createElement("div");
  dropzoneSpacer.className = "native-dropzone-spacer";
  leftCol.appendChild(dropzoneSpacer);

  let currentFiles: FileEntry[] = [];

  function onFilesLoaded(folders: FolderEntry[], files: FileEntry[]): void {
    console.timeEnd("[DocuLink] rename round-trip");
    currentFiles = files;
    currentFolders = folders;
    const t0 = performance.now();
    folderPanel.update(folders, files);
    fileTable.update(files, selectedFolderId);
    fileTable.updateFolders(folders);
    toolbar.updateFolders(currentFolders);
    console.log(`[DocuLink] DOM update: ${(performance.now() - t0).toFixed(1)}ms`);
  }

  function onOcrStatus(
    pdfId: string,
    status: string,
    message: string | undefined
  ): void {
    if (status === "error") {
      console.error(`[DocuLink] OCR error for pdf ${pdfId}:`, message ?? "(no details)");
    }
    const entry = currentFiles.find((f) => f.id === pdfId);
    if (entry) {
      entry.status = status;
      fileTable.update(currentFiles, selectedFolderId);
      const { selectedHasActiveOcr, anyOcrRunning } = computeToolbarState();
      toolbar.update(selectedIds.length, selectedHasActiveOcr, anyOcrRunning);
      fileTable.setSelectionLocked(anyOcrRunning);
    }
  }

  initHostBridge(onFilesLoaded, onOcrStatus);

  wireFileManagerUiReset({
    folderPanel,
    fileTable,
    toolbar,
    setSelectedFolderId: (folderId) => { selectedFolderId = folderId; },
    getCurrentFiles: () => currentFiles,
  });
}
