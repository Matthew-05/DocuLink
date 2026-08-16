import assert from "node:assert/strict";
import test from "node:test";

import { resolvePageRotation } from "../src/components/viewer/page-rotation.ts";


test("preserves intrinsic PDF rotation when no DocuLink rotation is stored", () => {
  assert.equal(resolvePageRotation(90, 0), 90);
  assert.equal(resolvePageRotation(270, 0), 270);
});

test("combines intrinsic and stored rotations", () => {
  assert.equal(resolvePageRotation(90, 90), 180);
  assert.equal(resolvePageRotation(270, 90), 0);
  assert.equal(resolvePageRotation(90, -90), 0);
});

test("normalizes rotations outside the canonical range", () => {
  assert.equal(resolvePageRotation(450, 360), 90);
  assert.equal(resolvePageRotation(-90, -90), 180);
});
