import assert from "node:assert/strict";
import test from "node:test";

import {
  getEdgeScrollStep,
  limitScrollStepToTarget,
} from "../src/components/viewer/drag-auto-scroller.ts";

test("does not scroll while the pointer is away from viewport edges", () => {
  assert.equal(getEdgeScrollStep(100, 0, 200), 0);
});

test("accelerates scrolling toward either viewport edge", () => {
  assert.equal(getEdgeScrollStep(24, 0, 200), -10);
  assert.equal(getEdgeScrollStep(176, 0, 200), 10);
  assert.equal(getEdgeScrollStep(0, 0, 200), -20);
  assert.equal(getEdgeScrollStep(200, 0, 200), 20);
});

test("caps scrolling when the pointer moves beyond the viewport", () => {
  assert.equal(getEdgeScrollStep(-100, 0, 200), -20);
  assert.equal(getEdgeScrollStep(300, 0, 200), 20);
});

test("stops scrolling when the dragged page edge reaches the pointer", () => {
  assert.equal(limitScrollStepToTarget(20, 180, 20, 300), 20);
  assert.equal(limitScrollStepToTarget(20, 180, 20, 190), 10);
  assert.equal(limitScrollStepToTarget(20, 180, 20, 170), 0);

  assert.equal(limitScrollStepToTarget(-20, 20, -100, 180), -20);
  assert.equal(limitScrollStepToTarget(-20, 20, 10, 180), -10);
  assert.equal(limitScrollStepToTarget(-20, 20, 30, 180), 0);
});
