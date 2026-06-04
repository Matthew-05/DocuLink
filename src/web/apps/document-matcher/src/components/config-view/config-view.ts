import type { FolderInfo } from "../../types/index.js";

const ALL_FOLDERS_ID = "__all__";

export interface StepFoldersCallbacks {
  onBack: () => void;
  onStart: (folderIds: string[]) => void;
}

export class StepFolders {
  private readonly _el: HTMLElement;
  private readonly _folders: FolderInfo[];
  private _startBtn!: HTMLButtonElement;
  private _allFoldersCheck!: HTMLInputElement;
  private _folderChecks = new Map<string, HTMLInputElement>();

  constructor(container: HTMLElement, folders: FolderInfo[], callbacks: StepFoldersCallbacks) {
    this._folders = folders;
    this._el = document.createElement("div");
    this._el.className = "wizard-step step-folders";
    container.appendChild(this._el);

    this._build(callbacks);
    this._validate();
  }

  private _build(callbacks: StepFoldersCallbacks): void {
    const body = document.createElement("div");
    body.className = "wizard-step__body config-view__body";

    const instructions = document.createElement("p");
    instructions.className = "step-folders__instructions";
    instructions.textContent = "Choose the folders DocuLink should search for matching PDFs.";
    body.appendChild(instructions);

    const folderSection = document.createElement("div");
    folderSection.className = "config-view__section";
    folderSection.innerHTML = `<div class="config-view__section-title">Search in Folders</div>`;

    const allLabel = document.createElement("label");
    allLabel.className = "config-view__folder-label";

    this._allFoldersCheck = document.createElement("input");
    this._allFoldersCheck.type = "checkbox";
    this._allFoldersCheck.checked = true;
    this._allFoldersCheck.addEventListener("change", () => {
      this._syncFolderChecks();
      this._validate();
    });

    allLabel.appendChild(this._allFoldersCheck);
    allLabel.appendChild(document.createTextNode("All folders"));
    folderSection.appendChild(allLabel);

    if (this._folders.length === 0) {
      const empty = document.createElement("p");
      empty.className = "config-view__empty";
      empty.textContent = "No folders found. All folders will search every PDF in this workbook.";
      folderSection.appendChild(empty);
    } else {
      for (const folder of this._folders) {
        const label = document.createElement("label");
        label.className = "config-view__folder-label";

        const cb = document.createElement("input");
        cb.type = "checkbox";
        cb.checked = true;
        cb.disabled = true;
        cb.addEventListener("change", () => {
          if (![...this._folderChecks.values()].every((check) => check.checked)) {
            this._allFoldersCheck.checked = false;
          }
          this._validate();
        });

        this._folderChecks.set(folder.id, cb);
        label.appendChild(cb);
        label.appendChild(document.createTextNode(folder.name));
        folderSection.appendChild(label);
      }
    }

    body.appendChild(folderSection);
    this._el.appendChild(body);

    const footer = document.createElement("div");
    footer.className = "wizard-step__footer";

    const backBtn = document.createElement("button");
    backBtn.className = "btn btn--ghost";
    backBtn.textContent = "Back";
    backBtn.addEventListener("click", () => callbacks.onBack());

    this._startBtn = document.createElement("button");
    this._startBtn.className = "btn btn--primary";
    this._startBtn.textContent = "Start Matching";
    this._startBtn.addEventListener("click", () => {
      if (this._allFoldersCheck.checked) {
        callbacks.onStart([ALL_FOLDERS_ID]);
        return;
      }

      const folderIds = [...this._folderChecks.entries()]
        .filter(([, cb]) => cb.checked)
        .map(([id]) => id);
      callbacks.onStart(folderIds);
    });

    footer.appendChild(backBtn);
    footer.appendChild(this._startBtn);
    this._el.appendChild(footer);
  }

  private _syncFolderChecks(): void {
    const useAll = this._allFoldersCheck.checked;
    for (const cb of this._folderChecks.values()) {
      cb.checked = useAll ? true : cb.checked;
      cb.disabled = useAll;
    }
  }

  private _validate(): void {
    const hasFolders = this._allFoldersCheck.checked || [...this._folderChecks.values()].some((cb) => cb.checked);
    this._startBtn.disabled = !hasFolders;
  }

  get element(): HTMLElement {
    return this._el;
  }

  remove(): void {
    this._el.remove();
  }
}
