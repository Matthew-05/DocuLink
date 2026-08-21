import assert from "node:assert/strict";
import test from "node:test";

import { renderPage } from "../src/components/viewer/page-renderer.ts";

class FakeElement {
  className = "";
  width = 0;
  height = 0;
  readonly style: Record<string, string> = {};
  readonly children: FakeElement[] = [];
  parent: FakeElement | null = null;

  appendChild(child: FakeElement): FakeElement {
    child.parent = this;
    this.children.push(child);
    return child;
  }

  insertBefore(child: FakeElement, reference: FakeElement): FakeElement {
    const index = this.children.indexOf(reference);
    assert.notEqual(index, -1);
    child.parent = this;
    this.children.splice(index, 0, child);
    return child;
  }

  replaceWith(replacement: FakeElement): void {
    assert.ok(this.parent);
    const index = this.parent.children.indexOf(this);
    assert.notEqual(index, -1);
    replacement.parent = this.parent;
    this.parent.children[index] = replacement;
    this.parent = null;
  }

  querySelector(selector: string): FakeElement | null {
    const matches = (element: FakeElement): boolean => {
      if (selector === "canvas") return element.className.includes("canvas");
      if (selector.startsWith(".")) {
        return element.className.split(/\s+/).includes(selector.slice(1));
      }
      return false;
    };

    for (const child of this.children) {
      if (matches(child)) return child;
      const descendant = child.querySelector(selector);
      if (descendant) return descendant;
    }
    return null;
  }

  getContext(): object {
    return {};
  }
}

test("keeps the PDF canvas outside search and link overlays", async () => {
  const originalDocument = globalThis.document;
  globalThis.document = {
    createElement: () => new FakeElement(),
  } as unknown as Document;

  try {
    const wrapper = new FakeElement();
    const overlays = new FakeElement();
    overlays.className = "viewer__overlays";

    const linkRectangle = new FakeElement();
    linkRectangle.className = "rect-draw__link";
    const searchCanvas = new FakeElement();
    searchCanvas.className = "search-match-canvas";
    overlays.appendChild(linkRectangle);
    overlays.appendChild(searchCanvas);
    wrapper.appendChild(overlays);

    const page = {
      getViewport: () => ({ width: 800, height: 1000 }),
      render: () => ({ promise: Promise.resolve() }),
      cleanup: () => undefined,
    };
    const doc = { getPage: async () => page };

    await renderPage(
      doc as never,
      1,
      wrapper as unknown as HTMLDivElement,
      1,
    );

    assert.equal(wrapper.children.length, 2);
    assert.equal(wrapper.children[0]?.className, "viewer__canvas");
    assert.equal(wrapper.children[1], overlays);
    assert.equal(overlays.children[0], linkRectangle);
    assert.equal(overlays.children[1], searchCanvas);
  } finally {
    globalThis.document = originalDocument;
  }
});
