import assert from "node:assert/strict";
import test from "node:test";

import { measureVisiblePages } from "../src/components/viewer/page-visibility.ts";

test("includes every page that intersects the viewport", () => {
  assert.deepEqual(
    measureVisiblePages(
      { top: 100, bottom: 700 },
      [
        { pageNumber: 1, top: -500, bottom: 250 },
        { pageNumber: 2, top: 270, bottom: 900 },
        { pageNumber: 3, top: 920, bottom: 1500 },
      ],
    ),
    [
      { pageNumber: 1, visibleHeight: 150 },
      { pageNumber: 2, visibleHeight: 430 },
    ],
  );
});

test("excludes pages that only touch a viewport boundary", () => {
  assert.deepEqual(
    measureVisiblePages(
      { top: 100, bottom: 700 },
      [
        { pageNumber: 1, top: -500, bottom: 100 },
        { pageNumber: 2, top: 700, bottom: 1200 },
      ],
    ),
    [],
  );
});
