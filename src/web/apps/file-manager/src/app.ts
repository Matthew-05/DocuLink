import type { FileEntry, FolderEntry } from "./types/index.js";
import { initHostBridge, sendSelectedFolder } from "./host-bridge.js";
import { FolderPanel } from "./components/folder-panel/folder-panel.js";
import { Dropzone } from "./components/dropzone/dropzone.js";
import { FileTable } from "./components/file-table/file-table.js";

export function mountApp(root: HTMLElement): void {
  root.className = "app";

  const leftCol = document.createElement("div");
  leftCol.className = "left-col";

  const rightCol = document.createElement("div");
  rightCol.className = "right-col";

  root.appendChild(leftCol);
  root.appendChild(rightCol);

  let selectedFolderId: string | null = null;

  const fileTable = new FileTable(rightCol);

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
    folderPanel.update(folders);
    fileTable.update(files, selectedFolderId);
  }

  initHostBridge(onFilesLoaded);
  sendSelectedFolder(null);
}
