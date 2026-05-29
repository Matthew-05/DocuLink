import * as esbuild from "esbuild";
import { copyFileSync, mkdirSync, rmSync, writeFileSync } from "fs";

const isProd = process.argv.includes("--prod");

rmSync("dist", { recursive: true, force: true });
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
    <link rel="icon" type="image/svg+xml" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><text y='75' font-size='75' font-weight='bold' fill='%234B5563'>D</text></svg>" />
    <link rel="stylesheet" href="index.css" />
  </head>
  <body>
    <div id="app"></div>
    <script type="module" src="index.js"></script>
  </body>
</html>
`
);

const sharedOptions = {
  bundle: true,
  minify: isProd,
  sourcemap: !isProd ? "inline" : false,
};

await esbuild.build({
  ...sharedOptions,
  entryPoints: ["src/main.ts"],
  outdir: "dist",
  entryNames: "index",
  chunkNames: "chunks/[name]-[hash]",
  format: "esm",
  splitting: true,
  platform: "browser",
});

await esbuild.build({
  ...sharedOptions,
  entryPoints: ["src/styles/main.css"],
  outfile: "dist/index.css",
});

console.log("[DocuLink] build complete");
