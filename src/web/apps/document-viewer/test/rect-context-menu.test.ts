import assert from "node:assert/strict";
import test from "node:test";

import { getViewportAwareMenuPosition } from "../src/components/viewer/rect-context-menu.ts";

test("keeps the menu at its anchor when there is room", () => {
  assert.deepEqual(
    getViewportAwareMenuPosition(100, 120, 140, 40, 800, 600),
    { left: 100, top: 120 },
  );
});

test("opens left and upward near the bottom-right viewport edge", () => {
  assert.deepEqual(
    getViewportAwareMenuPosition(790, 590, 140, 40, 800, 600),
    { left: 650, top: 550 },
  );
});

test("clamps menu placement to the viewport margin", () => {
  assert.deepEqual(
    getViewportAwareMenuPosition(2, 3, 140, 40, 800, 600),
    { left: 8, top: 8 },
  );
});
