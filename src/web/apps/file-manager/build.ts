import { buildWebApp } from "../../build-app.ts";

await buildWebApp({
  appUrl: import.meta.url,
  title: "Talliark — Manage Files",
  format: "iife",
  completionMessage: "[Talliark] file-manager build complete",
  production: process.argv.includes("--prod"),
});
