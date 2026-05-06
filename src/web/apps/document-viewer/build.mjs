import * as esbuild from "esbuild";
import { copyFileSync, mkdirSync, writeFileSync } from "fs";

const isProd = process.argv.includes("--prod");
const isWatch = process.argv.includes("--watch");

mkdirSync("dist", { recursive: true });

copyFileSync(
  "../../node_modules/pdfjs-dist/build/pdf.worker.min.mjs",
  "dist/pdf.worker.min.mjs"
);

writeFileSync(
  "dist/index.html",
  `<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>DocuLink — Document Viewer</title>
    <link rel="stylesheet" href="index.css" />
  </head>
  <body>
    <div id="app"></div>
    <script src="index.js"></script>
  </body>
</html>
`
);

const sharedOptions = {
  bundle: true,
  minify: isProd,
  sourcemap: !isProd ? "inline" : false,
};

const jsCtx = await esbuild.context({
  ...sharedOptions,
  entryPoints: ["src/main.ts"],
  outfile: "dist/index.js",
  format: "iife",
  platform: "browser",
});

const cssCtx = await esbuild.context({
  ...sharedOptions,
  entryPoints: ["src/styles/main.css"],
  outfile: "dist/index.css",
});

if (isWatch) {
  await jsCtx.watch();
  await cssCtx.watch();
  console.log("[DocuLink] esbuild watching — Ctrl+C to stop");
} else {
  await jsCtx.rebuild();
  await cssCtx.rebuild();
  await jsCtx.dispose();
  await cssCtx.dispose();
  console.log("[DocuLink] build complete");
}
