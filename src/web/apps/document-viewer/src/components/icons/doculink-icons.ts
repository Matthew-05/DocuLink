const SVG_NAMESPACE = "http://www.w3.org/2000/svg";

function createSvgElement<K extends keyof SVGElementTagNameMap>(
  tagName: K,
  attributes: Record<string, string>,
): SVGElementTagNameMap[K] {
  const element = document.createElementNS(SVG_NAMESPACE, tagName);
  for (const [name, value] of Object.entries(attributes)) {
    element.setAttribute(name, value);
  }
  return element;
}

function createIcon(className: string, viewBox: string): SVGSVGElement {
  return createSvgElement("svg", {
    "aria-hidden": "true",
    class: className,
    focusable: "false",
    viewBox,
  });
}

/** Original DocuLink layered-document artwork used for document onboarding. */
export function createDocumentIcon(): SVGSVGElement {
  const icon = createIcon("doculink-icon doculink-icon--document", "0 0 120 112");

  const halo = createSvgElement("circle", {
    class: "doculink-icon__halo",
    cx: "57",
    cy: "59",
    r: "47",
  });

  const backPage = createSvgElement("rect", {
    class: "doculink-icon__back-page",
    height: "76",
    rx: "10",
    transform: "rotate(-7 54 57)",
    width: "56",
    x: "26",
    y: "19",
  });

  const page = createSvgElement("path", {
    class: "doculink-icon__page",
    d: "M39 10.5h31l17 17V83a9.5 9.5 0 0 1-9.5 9.5h-38A9.5 9.5 0 0 1 30 83V20a9.5 9.5 0 0 1 9-9.5Z",
  });
  const fold = createSvgElement("path", {
    class: "doculink-icon__fold",
    d: "M70 11v16.5h16.5",
  });

  const lines = createSvgElement("g", { class: "doculink-icon__document-lines" });
  lines.append(
    createSvgElement("path", { d: "M43 45h29" }),
    createSvgElement("path", { d: "M43 57h24" }),
    createSvgElement("path", { d: "M43 69h16" }),
  );

  icon.append(halo, backPage, page, fold, lines);
  return icon;
}

/** Original DocuLink stacked-folder glyph used for Manage Files actions. */
export function createManageFilesIcon(): SVGSVGElement {
  const icon = createIcon("doculink-icon doculink-icon--manage-files", "0 0 24 24");
  icon.append(
    createSvgElement("path", {
      class: "doculink-icon__folder-back",
      d: "M5.25 5.25h5l1.7 2H20v9.5H5.25Z",
    }),
    createSvgElement("path", {
      class: "doculink-icon__folder-middle",
      d: "M3.25 7.5h5l1.7 2H18v9.25H3.25Z",
    }),
    createSvgElement("path", {
      class: "doculink-icon__folder-front",
      d: "M1.75 10h5l1.7 2h7.8v8.25H1.75Z",
    }),
  );
  return icon;
}
