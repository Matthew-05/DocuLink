const STEP_LABELS = ["Select Ranges", "Output Columns", "Folders", "Results"] as const;

export class StepIndicator {
  private readonly _el: HTMLElement;
  private readonly _stepEls: HTMLElement[] = [];

  constructor(container: HTMLElement) {
    this._el = document.createElement("div");
    this._el.className = "step-indicator";

    STEP_LABELS.forEach((label, i) => {
      if (i > 0) {
        const connector = document.createElement("div");
        connector.className = "step-indicator__connector";
        this._el.appendChild(connector);
      }

      const step = document.createElement("div");
      step.className = "step-indicator__step";

      const num = document.createElement("div");
      num.className = "step-indicator__num";
      num.textContent = String(i + 1);

      const lbl = document.createElement("div");
      lbl.className = "step-indicator__label";
      lbl.textContent = label;

      step.appendChild(num);
      step.appendChild(lbl);
      this._el.appendChild(step);
      this._stepEls.push(step);
    });

    container.appendChild(this._el);
  }

  /** @param step 1-based step number */
  setStep(step: number): void {
    this._stepEls.forEach((el, i) => {
      el.classList.toggle("step-indicator__step--active", i + 1 === step);
      el.classList.toggle("step-indicator__step--done", i + 1 < step);
    });
  }

  get element(): HTMLElement {
    return this._el;
  }
}
