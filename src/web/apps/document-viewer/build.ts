import { buildWebApp } from "../../build-app.ts";

await buildWebApp({
  appUrl: import.meta.url,
  title: "DocuLink — Document Viewer",
  format: "esm",
  completionMessage: "[DocuLink] build complete",
  cleanDist: true,
  copyPdfWorker: true,
  production: process.argv.includes("--prod"),
  splitting: true,
});
