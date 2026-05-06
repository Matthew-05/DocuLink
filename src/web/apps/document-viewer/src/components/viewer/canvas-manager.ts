/**
 * Ensures the container holds exactly `total` canvas elements with
 * `data-page` attributes. Returns the (possibly reused) array of canvases.
 *
 * Recreates the full set when the count changes; reuses the existing set
 * when the count matches to avoid unnecessary DOM thrash.
 */
export function ensureCanvases(
  container: HTMLElement,
  total: number
): HTMLCanvasElement[] {
  const existing = Array.from(
    container.querySelectorAll<HTMLCanvasElement>("canvas[data-page]")
  );

  if (existing.length === total) return existing;

  container.replaceChildren();
  const result: HTMLCanvasElement[] = [];

  for (let i = 1; i <= total; i++) {
    const canvas = document.createElement("canvas");
    canvas.className = "viewer__page";
    canvas.dataset["page"] = String(i);
    container.appendChild(canvas);
    result.push(canvas);
  }

  return result;
}
