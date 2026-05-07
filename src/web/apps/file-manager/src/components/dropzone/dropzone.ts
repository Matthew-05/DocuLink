import { sendAddFiles, type AddFilePayload } from "../../host-bridge.js";

export class Dropzone {
  private readonly _root: HTMLElement;
  private readonly _fileInput: HTMLInputElement;
  private _activeFolderId: string | null = null;

  constructor(container: HTMLElement) {
    this._root = document.createElement("div");
    this._root.className = "dropzone";
    this._root.setAttribute("role", "button");
    this._root.setAttribute("tabindex", "0");

    const icon = document.createElement("div");
    icon.className = "dropzone__icon";
    icon.textContent = "📂";

    const label = document.createElement("p");
    label.className = "dropzone__label";
    label.textContent = "Drop PDFs or folders here";

    const sub = document.createElement("p");
    sub.className = "dropzone__sub";
    sub.textContent = "or click to browse";

    this._fileInput = document.createElement("input");
    this._fileInput.type = "file";
    this._fileInput.accept = ".pdf,application/pdf";
    this._fileInput.multiple = true;
    this._fileInput.style.display = "none";

    this._root.appendChild(icon);
    this._root.appendChild(label);
    this._root.appendChild(sub);
    this._root.appendChild(this._fileInput);
    container.appendChild(this._root);

    this._root.addEventListener("click", () => this._fileInput.click());
    this._root.addEventListener("keydown", (e) => {
      if (e.key === "Enter" || e.key === " ") this._fileInput.click();
    });
    this._fileInput.addEventListener("change", () => this._onFileInputChange());

    // Browsers require dragenter + dragover + preventDefault for drop to fire; WebView2/OLE
    // often still routes Explorer drops to native code — the C# host imports those paths.
    this._root.addEventListener("dragenter", (e) => {
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = "copy";
    });
    this._root.addEventListener("dragover", (e) => {
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = "copy";
      this._root.classList.add("dropzone--over");
    });
    this._root.addEventListener("dragleave", (e) => {
      if (!this._root.contains(e.relatedTarget as Node))
        this._root.classList.remove("dropzone--over");
    });
    this._root.addEventListener("drop", (e) => {
      e.preventDefault();
      this._root.classList.remove("dropzone--over");
      if (e.dataTransfer) this._handleDrop(e.dataTransfer);
    });
  }

  setActiveFolderId(id: string | null): void {
    this._activeFolderId = id;
  }

  private _onFileInputChange(): void {
    const files = this._fileInput.files;
    if (!files || files.length === 0) return;
    const pending: Promise<AddFilePayload>[] = [];
    for (let i = 0; i < files.length; i++) {
      const file = files[i];
      if (file) pending.push(this._readFile(file, this._activeFolderId));
    }
    void Promise.all(pending).then((payloads) => {
      if (payloads.length > 0) sendAddFiles(payloads);
    });
    this._fileInput.value = "";
  }

  private _handleDrop(dt: DataTransfer): void {
    const items = Array.from(dt.items);
    const pending: Promise<AddFilePayload[]>[] = [];

    for (const item of items) {
      if (item.kind !== "file") continue;
      const entry = item.webkitGetAsEntry?.();
      if (!entry) {
        const file = item.getAsFile();
        if (file && file.name.toLowerCase().endsWith(".pdf")) {
          pending.push(
            this._readFile(file, this._activeFolderId).then((p) => [p])
          );
        }
        continue;
      }

      if (entry.isFile) {
        if (entry.name.toLowerCase().endsWith(".pdf")) {
          pending.push(
            this._readFileEntry(entry as FileSystemFileEntry, this._activeFolderId).then(
              (p) => (p ? [p] : [])
            )
          );
        }
      } else if (entry.isDirectory) {
        pending.push(
          this._readDirectory(entry as FileSystemDirectoryEntry, entry.name)
        );
      }
    }

    // Some WebView2 builds expose files on DataTransfer.items but not Entries; use File list.
    if (pending.length === 0 && dt.files && dt.files.length > 0) {
      for (let i = 0; i < dt.files.length; i++) {
        const file = dt.files[i];
        if (file && file.name.toLowerCase().endsWith(".pdf")) {
          pending.push(
            this._readFile(file, this._activeFolderId).then((p) => [p])
          );
        }
      }
    }

    void Promise.all(pending).then((groups) => {
      const all = groups.flat();
      if (all.length > 0) sendAddFiles(all);
    });
  }

  private _readDirectory(
    dir: FileSystemDirectoryEntry,
    folderName: string
  ): Promise<AddFilePayload[]> {
    return new Promise((resolve) => {
      const reader = dir.createReader();
      const allEntries: FileSystemEntry[] = [];

      const readBatch = (): void => {
        reader.readEntries((entries) => {
          if (entries.length === 0) {
            const pdfEntries = allEntries.filter(
              (e) => e.isFile && e.name.toLowerCase().endsWith(".pdf")
            ) as FileSystemFileEntry[];

            const pending = pdfEntries.map((fe) =>
              this._readFileEntry(fe, null, folderName)
            );

            void Promise.all(pending).then((results) => {
              resolve(results.filter((r): r is AddFilePayload => r !== null));
            });
          } else {
            allEntries.push(...entries);
            readBatch();
          }
        });
      };

      readBatch();
    });
  }

  private _readFileEntry(
    entry: FileSystemFileEntry,
    folderId: string | null,
    _folderNameHint?: string
  ): Promise<AddFilePayload | null> {
    return new Promise((resolve) => {
      entry.file(
        (file) => {
          void this._readFile(file, folderId, _folderNameHint).then(resolve);
        },
        () => resolve(null)
      );
    });
  }

  private _readFile(
    file: File,
    folderId: string | null,
    _folderNameHint?: string
  ): Promise<AddFilePayload> {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => {
        const dataUrl = reader.result as string;
        const base64 = dataUrl.split(",")[1] ?? "";
        const payload: AddFilePayload = { name: file.name, base64 };
        if (folderId) payload.folderId = folderId;
        else if (_folderNameHint) {
          // Folder name hint is resolved by the host via add-folder logic.
          // We embed it as a sentinel so the host can create the folder.
          // Since the host expects a folderId (GUID), we use a special prefix
          // that the host recognises as a "create-and-assign" directive.
          // For simplicity in this implementation, pass the hint in the name
          // and let ManageFilesService handle it — but the cleanest approach is
          // to pre-create the folder before sending files. We do that here:
          payload.folderId = `__new__:${_folderNameHint}`;
        }
        resolve(payload);
      };
      reader.onerror = () => reject(new Error(`Failed to read file: ${file.name}`));
      reader.readAsDataURL(file);
    });
  }
}
