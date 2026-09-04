import { buildWebApp } from "../../build-app.ts";

await buildWebApp({
  appUrl: import.meta.url,
  title: "DocuLink — Match Documents",
  format: "iife",
  completionMessage: "[DocuLink] document-matcher build complete",
  copyPdfWorker: true,
  production: process.argv.includes("--prod"),
});
