import assert from "node:assert/strict";
import test from "node:test";

import {
  MIN_DRAG_PX,
  resizeHandleFromDrag,
  resizeRectFromHandle,
} from "../src/components/viewer/rect-utils.ts";
import type { NormalizedRect } from "../src/types/index.ts";

/** A 200x100 page stand-in; only the offset dimensions are read. */
const page = { offsetWidth: 200, offsetHeight: 100 } as HTMLElement;

/** Centred rect: x 50..150 px, y 25..75 px. */
const rect: NormalizedRect = { x: 0.25, y: 0.25, width: 0.5, height: 0.5 };

function pixels(r: NormalizedRect) {
  return {
    left:   Math.round(r.x * 200),
    top:    Math.round(r.y * 100),
    right:  Math.round((r.x + r.width) * 200),
    bottom: Math.round((r.y + r.height) * 100),
  };
}

test("resizing within bounds moves only the dragged corner", () => {
  assert.deepEqual(
    pixels(resizeRectFromHandle(page, rect, "se", 180, 90)),
    { left: 50, top: 25, right: 180, bottom: 90 },
  );
  assert.deepEqual(
    pixels(resizeRectFromHandle(page, rect, "nw", 20, 10)),
    { left: 20, top: 10, right: 150, bottom: 75 },
  );
});

test("a corner dragged past its anchor flips the rectangle", () => {
  // "se" pulled up and to the left of the fixed top-left corner (50, 25).
  assert.deepEqual(
    pixels(resizeRectFromHandle(page, rect, "se", 10, 5)),
    { left: 10, top: 5, right: 50, bottom: 25 },
  );
  // "nw" pushed past the fixed bottom-right corner (150, 75).
  assert.deepEqual(
    pixels(resizeRectFromHandle(page, rect, "nw", 190, 95)),
    { left: 150, top: 75, right: 190, bottom: 95 },
  );
});

test("a flip on one axis only mirrors that axis", () => {
  // "ne" dragged left of the anchor x (50) but still above the anchor y (75).
  assert.deepEqual(
    pixels(resizeRectFromHandle(page, rect, "ne", 10, 40)),
    { left: 10, top: 40, right: 50, bottom: 75 },
  );
});

test("the rectangle keeps a minimum size on both sides of the anchor", () => {
  const collapsed = resizeRectFromHandle(page, rect, "se", 50, 25);
  assert.deepEqual(pixels(collapsed), {
    left: 50, top: 25, right: 50 + MIN_DRAG_PX, bottom: 25 + MIN_DRAG_PX,
  });

  // Just short of the anchor: the minimum is taken on the pointer's side.
  const nearlyFlipped = resizeRectFromHandle(page, rect, "se", 49, 24);
  assert.deepEqual(pixels(nearlyFlipped), {
    left: 50 - MIN_DRAG_PX, top: 25 - MIN_DRAG_PX, right: 50, bottom: 25,
  });
});

test("the minimum grows inward when the anchor sits on a page edge", () => {
  const atOrigin: NormalizedRect = { x: 0, y: 0, width: 0.5, height: 0.5 };
  // "nw" is dragged onto its anchor's far side; the anchor here is (100, 50).
  const flat = resizeRectFromHandle(page, atOrigin, "se", 0, 0);
  assert.deepEqual(pixels(flat), {
    left: 0, top: 0, right: MIN_DRAG_PX, bottom: MIN_DRAG_PX,
  });

  const atEdge: NormalizedRect = { x: 0.5, y: 0.5, width: 0.5, height: 0.5 };
  const pinned = resizeRectFromHandle(page, atEdge, "nw", 200, 100);
  assert.deepEqual(pixels(pinned), {
    left: 200 - MIN_DRAG_PX, top: 100 - MIN_DRAG_PX, right: 200, bottom: 100,
  });
});

test("the live handle follows the pointer across the anchor", () => {
  assert.equal(resizeHandleFromDrag(page, rect, "se", 180, 90), "se");
  assert.equal(resizeHandleFromDrag(page, rect, "se", 10, 5),   "nw");
  assert.equal(resizeHandleFromDrag(page, rect, "se", 10, 90),  "sw");
  assert.equal(resizeHandleFromDrag(page, rect, "nw", 190, 10), "ne");
});
