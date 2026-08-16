export type ModalActionVariant = "primary" | "secondary" | "danger";

export interface ModalAction {
  value: string;
  label: string;
  variant?: ModalActionVariant;
  autofocus?: boolean;
}

export interface ModalOptions {
  title: string;
  content: Node;
  actions: ModalAction[];
  labelledBy?: string;
}

let nextModalId = 1;

/** Shared native-dialog wrapper used by every DocuLink web app modal. */
export class Modal {
  private readonly _dialog: HTMLDialogElement;
  private readonly _title: HTMLHeadingElement;
  private readonly _content: HTMLDivElement;
  private readonly _actions: HTMLDivElement;
  private _resolve: ((value: string | null) => void) | null = null;

  constructor() {
    const titleId = `doculink-modal-title-${nextModalId++}`;
    this._dialog = document.createElement("dialog");
    this._dialog.className = "doculink-modal";
    this._dialog.setAttribute("aria-labelledby", titleId);

    const surface = document.createElement("div");
    surface.className = "doculink-modal__surface";

    this._title = document.createElement("h2");
    this._title.id = titleId;
    this._title.className = "doculink-modal__title";

    this._content = document.createElement("div");
    this._content.className = "doculink-modal__content";

    this._actions = document.createElement("div");
    this._actions.className = "doculink-modal__actions";

    surface.append(this._title, this._content, this._actions);
    this._dialog.append(surface);
    this._dialog.addEventListener("cancel", (event) => {
      event.preventDefault();
      this.close();
    });
    this._dialog.addEventListener("click", (event) => {
      if (event.target === this._dialog) this.close();
    });
    document.body.append(this._dialog);
  }

  show(options: ModalOptions): Promise<string | null> {
    if (this._resolve) this.close();

    this._title.textContent = options.title;
    if (options.labelledBy) {
      this._dialog.setAttribute("aria-labelledby", options.labelledBy);
    } else {
      this._dialog.setAttribute("aria-labelledby", this._title.id);
    }
    this._content.replaceChildren(options.content);
    this._actions.replaceChildren();

    for (const action of options.actions) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `doculink-modal__button doculink-modal__button--${action.variant ?? "secondary"}`;
      button.textContent = action.label;
      button.autofocus = action.autofocus === true;
      button.addEventListener("click", () => this._finish(action.value));
      this._actions.append(button);
    }

    this._dialog.showModal();
    return new Promise<string | null>((resolve) => { this._resolve = resolve; });
  }

  close(): void {
    this._finish(null);
  }

  private _finish(value: string | null): void {
    const resolve = this._resolve;
    this._resolve = null;
    if (this._dialog.open) this._dialog.close();
    resolve?.(value);
  }
}
