import type { FileEntry, FolderEntry } from "./types/index.js";
import { initHostBridge, sendSelectedFolder, sendRemoveFile, sendMoveFile } from "./host-bridge.js";
import { FolderPanel } from "./components/folder-panel/folder-panel.js";
import { Dropzone } from "./components/dropzone/dropzone.js";
import { FileTable } from "./components/file-table/file-table.js";
import { TableToolbar } from "./components/table-toolbar/table-toolbar.js";

export function mountApp(root: HTMLElement): void {
  root.className = "app";

  const leftCol = document.createElement("div");
  leftCol.className = "left-col";

  const rightCol = document.createElement("div");
  rightCol.className = "right-col";

  root.appendChild(leftCol);
  root.appendChild(rightCol);

  let selectedFolderId: string | null = null;
  let currentFolders: FolderEntry[] = [];

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
    onFilterChange(text: string) {
      fileTable.setFilter(text);
    },
  });

  const fileTable = new FileTable(rightCol, {
    onSelectionChange(ids: string[]) {
      toolbar.update(ids.length);
    },
  });

  const folderPanel = new FolderPanel(leftCol, {
    onSelectionChange(folderId: string | null) {
      selectedFolderId = folderId;
      sendSelectedFolder(folderId);
      dropzone.setActiveFolderId(folderId);
      fileTable.update(currentFiles, selectedFolderId);
    },
  });

  const dropzone = new Dropzone(leftCol);

  let currentFiles: FileEntry[] = [];

  function onFilesLoaded(folders: FolderEntry[], files: FileEntry[]): void {
    currentFiles = files;
    currentFolders = folders;
    folderPanel.update(folders, files);
    fileTable.update(files, selectedFolderId);
    fileTable.updateFolders(folders);
    toolbar.updateFolders(currentFolders);
  }

  initHostBridge(onFilesLoaded);
  sendSelectedFolder(null);
}
