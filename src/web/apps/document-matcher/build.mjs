import * as esbuild from "esbuild";
import { mkdirSync, writeFileSync } from "fs";

const isProd = process.argv.includes("--prod");

mkdirSync("dist", { recursive: true });

writeFileSync(
  "dist/index.html",
  `<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>DocuLink — Match Documents</title>
    <link rel="icon" type="image/svg+xml" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><text y='75' font-size='75' font-weight='bold' fill='%234B5563'>D</text></svg>" />
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

await esbuild.build({
  ...sharedOptions,
  entryPoints: ["src/main.ts"],
  outfile: "dist/index.js",
  format: "iife",
  platform: "browser",
});

await esbuild.build({
  ...sharedOptions,
  entryPoints: ["src/styles/main.css"],
  outfile: "dist/index.css",
});

console.log("[DocuLink] document-matcher build complete");
