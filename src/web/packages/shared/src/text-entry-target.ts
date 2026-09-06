/**
 * Whether an event target owns the keystroke because the user is typing into
 * it. Both apps bind document-level shortcuts — Excel cell navigation in the
 * viewer, select-all in the file manager — and every one of them has to stand
 * down inside a text field, where the same keys mean something to the field.
 */
export function isTextEntryTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;

  if (target.isContentEditable) return true;

  const tag = target.tagName;
  if (tag === "TEXTAREA" || tag === "SELECT") return true;

  if (tag === "INPUT") {
    // Buttons and checkboxes render as INPUT but hold no text, so a shortcut
    // over one of them is still the page's to handle.
    const type = (target as HTMLInputElement).type.toLowerCase();
    return type !== "button" && type !== "submit" && type !== "reset" && type !== "checkbox" && type !== "radio";
  }

  return false;
}
