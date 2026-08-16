import assert from "node:assert/strict";
import test from "node:test";

import { resolveTableCopyPages } from "../src/components/table-copy-modal/table-copy-pages.ts";

test("copies to every page after the source page", () => {
  assert.deepEqual(resolveTableCopyPages(1, 5, "below"), [2, 3, 4]);
});

test("uses an inclusive page range and skips the source page", () => {
  assert.deepEqual(resolveTableCopyPages(2, 6, "range", 2, 5), [1, 3, 4]);
});

test("rejects invalid or source-only ranges", () => {
  assert.deepEqual(resolveTableCopyPages(2, 6, "range", 4, 3), []);
  assert.deepEqual(resolveTableCopyPages(2, 6, "range", 3, 3), []);
});
