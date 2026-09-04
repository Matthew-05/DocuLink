import { buildWebApp } from "../../build-app.ts";

await buildWebApp({
  appUrl: import.meta.url,
  title: "DocuLink — Manage Files",
  format: "iife",
  completionMessage: "[DocuLink] file-manager build complete",
  production: process.argv.includes("--prod"),
});
