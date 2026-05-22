import type { FileEntry } from "./types/index.js";
import type { FolderPanel } from "./components/folder-panel/folder-panel.js";
import type { FileTable } from "./components/file-table/file-table.js";
import type { TableToolbar } from "./components/table-toolbar/table-toolbar.js";
import type { Dropzone } from "./components/dropzone/dropzone.js";
import { registerUiResetHandler, sendSelectedFolder } from "./host-bridge.js";

export interface FileManagerUiHandles {
  folderPanel: FolderPanel;
  fileTable: FileTable;
  toolbar: TableToolbar;
  dropzone: Dropzone;
  setSelectedFolderId: (folderId: string | null) => void;
  getCurrentFiles: () => FileEntry[];
}

/** Clears transient UI state (selection, filters, folder pick) back to defaults. */
export function resetFileManagerUi(handles: FileManagerUiHandles): void {
  handles.setSelectedFolderId(null);
  handles.folderPanel.reset();
  handles.fileTable.reset();
  handles.toolbar.reset();
  handles.dropzone.setActiveFolderId(null);
  sendSelectedFolder(null);
  handles.fileTable.update(handles.getCurrentFiles(), null);
}

/** Listens for the host `reset-ui` message and runs {@link resetFileManagerUi}. */
export function wireFileManagerUiReset(handles: FileManagerUiHandles): void {
  registerUiResetHandler(() => resetFileManagerUi(handles));
}
