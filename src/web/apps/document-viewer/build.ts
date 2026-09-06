import { buildWebApp } from "../../build-app.ts";

await buildWebApp({
  appUrl: import.meta.url,
  title: "Talliark — Document Viewer",
  format: "esm",
  completionMessage: "[Talliark] build complete",
  cleanDist: true,
  copyPdfWorker: true,
  production: process.argv.includes("--prod"),
  splitting: true,
});
