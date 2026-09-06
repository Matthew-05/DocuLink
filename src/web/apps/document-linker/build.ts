import { buildWebApp } from "../../build-app.ts";

await buildWebApp({
  appUrl: import.meta.url,
  title: "Talliark — Link Documents",
  format: "iife",
  completionMessage: "[Talliark] document-linker build complete",
  copyPdfWorker: true,
  production: process.argv.includes("--prod"),
});
