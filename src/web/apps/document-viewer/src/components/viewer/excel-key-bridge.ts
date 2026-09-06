import { sendExcelNavigate, sendUndoLinkCreation } from "../../host-bridge.js";
import { isTextEntryTarget } from "@talliark/shared";

/**
 * Forwards Excel's cell-navigation and undo keystrokes from the viewer to the host.
 *
 * The WebView swallows these keys by default — Tab walks the browser focus ring and
 * Ctrl+Z drives the DOM undo stack — which strands a user who is drawing rectangles
 * and wants to keep moving the Excel cursor without reaching for the grid. This
 * bridge captures them and posts an `excel-navigate` / `undo-link-creation` message
 * instead.
 *
 * No navigation semantics live here. The host decides direction, honours the
 * move-after-return setting, tracks the Tab-run anchor and cycles inside multi-cell
 * selections, because only it can see the Excel application state.
 */

/**
 * Installs the capture-phase key listener. Returns a disposer that removes it.
 */
export function attachExcelKeyBridge(): () => void {
  const onKeyDown = (e: KeyboardEvent): void => {
    if (e.defaultPrevented) return;
    if (isTextEntryTarget(e.target)) return;

    // Ctrl+Z / Cmd+Z — ask the host to undo the true latest action. The host gives
    // Excel's native stack priority over its rectangle-creation history. Shift is
    // excluded so Ctrl+Shift+Z stays free; redo is deliberately out of scope.
    if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === "z") {
      e.preventDefault();
      e.stopPropagation();
      sendUndoLinkCreation();
      return;
    }

    // Navigation keys carry no modifier other than Shift; Ctrl+Enter and friends
    // mean something different in Excel and are left alone.
    if (e.ctrlKey || e.metaKey || e.altKey) return;

    if (e.key === "Tab") {
      e.preventDefault();
      e.stopPropagation();
      sendExcelNavigate("tab", e.shiftKey);
      return;
    }

    if (e.key === "Enter") {
      e.preventDefault();
      e.stopPropagation();
      sendExcelNavigate("enter", e.shiftKey);
    }
  };

  document.addEventListener("keydown", onKeyDown, true);

  return () => document.removeEventListener("keydown", onKeyDown, true);
}
