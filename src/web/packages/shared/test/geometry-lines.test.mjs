import assert from "node:assert/strict";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import ts from "typescript";

const packageRoot = dirname(dirname(fileURLToPath(import.meta.url)));
const outdir = join(packageRoot, "test", ".tmp-geometry-lines");
const outfile = join(outdir, "char-entries.mjs");
const sourceFile = join(packageRoot, "src", "char-entries.ts");

await rm(outdir, { recursive: true, force: true });
await mkdir(outdir, { recursive: true });

const source = await readFile(sourceFile, "utf8");
const transpiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ESNext,
    target: ts.ScriptTarget.ES2022,
    verbatimModuleSyntax: true,
  },
});
await writeFile(outfile, transpiled.outputText, "utf8");

const { buildCharEntriesFromGeometry } = await import(pathToFileURL(outfile).href);

const geometry = {
  version: 1,
  coordinateSpace: "normalized",
  pages: [
    {
      pageIndex: 0,
      characters: [
        { char: "7", x: 0.1, y: 0.1, width: 0.02, height: 0.04, lineIndex: 0 },
        { char: "n", x: 0.1, y: 0.115, width: 0.02, height: 0.025, lineIndex: 1 },
      ],
    },
  ],
};

const entries = buildCharEntriesFromGeometry(geometry).get(0);
assert.ok(entries);
assert.deepEqual(entries.map((entry) => entry.lineIndex), [0, 1]);

console.log("[DocuLink] geometry line tests passed");
await rm(outdir, { recursive: true, force: true });
