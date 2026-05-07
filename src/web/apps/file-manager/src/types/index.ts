export interface FolderEntry {
  id: string;
  name: string;
}

export interface FileEntry {
  id: string;
  name: string;
  folderId?: string;
  status: string;
  fileSizeBytes: number;
  dateAdded: string;
}

export interface AppState {
  folders: FolderEntry[];
  files: FileEntry[];
  selectedFolderId: string | null;
}
