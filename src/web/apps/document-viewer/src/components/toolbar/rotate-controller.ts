export class RotateController {
  readonly element: HTMLElement;

  private _cwCallbacks:  Array<() => void> = [];
  private _ccwCallbacks: Array<() => void> = [];

  constructor() {
    this.element = document.createElement("div");
    this.element.className = "rotate-controller";

    const ccwBtn = document.createElement("button");
    ccwBtn.className = "rotate-controller__btn";
    ccwBtn.title = "Rotate page counter-clockwise";
    ccwBtn.textContent = "↺";
    ccwBtn.addEventListener("click", () => {
      for (const cb of this._ccwCallbacks) cb();
    });

    const cwBtn = document.createElement("button");
    cwBtn.className = "rotate-controller__btn";
    cwBtn.title = "Rotate page clockwise";
    cwBtn.textContent = "↻";
    cwBtn.addEventListener("click", () => {
      for (const cb of this._cwCallbacks) cb();
    });

    this.element.append(ccwBtn, cwBtn);
  }

  onRotateCcw(cb: () => void): void {
    this._ccwCallbacks.push(cb);
  }

  onRotateCw(cb: () => void): void {
    this._cwCallbacks.push(cb);
  }
}
