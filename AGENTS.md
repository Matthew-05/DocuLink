# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Project Overview

**DocuLink** is an Excel VSTO add-in that embeds PDFs in workbooks and provides interactive document viewing and annotation capabilities. The system spans three runtime boundaries:

- **C# (src/DocuLink.Addin/)** — Excel COM integration, WebView2 host, workbook lifecycle
- **TypeScript (src/web/)** — Task pane and viewer UIs (document-viewer, file-manager apps)
- **Python (src/python/)** — OCR engine using Tesseract and Ghostscript for text extraction and geometry

Cross-boundary data is strictly versioned via JSON/XML contracts in `contracts/` — never duplicate shapes across languages.

## Architecture & Code Organization

### Layered Segregation (Enforce Strictly)

**C# modules** (`src/DocuLink.Addin/Modules/`):
- **Addin/** — Composition wiring only; keep `ThisAddIn` thin
- **UI/** — Ribbon UI callbacks; delegate to services
- **WebView/** — WebView2 hosting and postMessage routing; no business logic
- **CustomXml/** — Models and XML serialization for workbook storage
- **Services/** — Domain service orchestration (e.g. adding links, managing PDFs)
- **Infrastructure/** — OCR client, logging, settings, paths

**Web apps** (`src/web/apps/{document-viewer,file-manager}/`):
- No direct Python calls or file path manipulation
- Only consume WebView2 postMessage events from C# host
- Delegate all heavy work to the host

**Python** (`src/python/`):
- OCR and PDF processing only; no Excel/WebView APIs
- One module per engine (Tesseract in `engines/tesseract/`, Ghostscript in `engines/ghostscript/`)
- Schemas and models in `src/python/schemas/`

### Single Responsibility — New Files

When adding logic:
1. **Search first** — Does an existing file provide or partially provide this function?
   - If yes, modify the existing file rather than creating a new one
   - If logic differs significantly, consider creating a new folder if it's a major concern
2. **Determine folder** — Is there an existing folder that semantically fits?
   - If yes, create the file there
   - If no and it's truly new/distinct, create a new folder

The test (quick checklist):
- [ ] Does this file have a single clear purpose?
- [ ] Is any logic duplicated from another layer or app? Extract or call shared code.
- [ ] Would a new contributor find the right folder in under a minute?

### Contracts — The Single Source of Truth

All cross-boundary data shapes are versioned in `contracts/`:

| File | Describes |
|------|-----------|
| `doculink-storage-content-v1.xsd` | PDF/folder catalogue in Custom XML |
| `doculink-storage-links-v1.xsd` | Linked rectangles in Custom XML |
| `webview-messages-v1.json` | C# ↔ Web postMessage protocol |
| `python-worker-v1.json` | C# ↔ Python worker protocol (stdin/stdout NDJSON) |
| `text-geometry-v1.json` | OCR text layout data (gzip + base64 in messages) |

**Before implementing** a new message type or storage field, define its shape in the contract first, then implement.

**After serialization changes**, update the matching contract (XSD, JSON schema, and sample files).

**Never** hand-duplicate a shape across languages (C# + TS + Python). Extract to a contract first.

## Development Commands

### Web (TypeScript)

```bash
# Build all workspaces
npm run build --workspaces

# Or build a specific app
cd src/web/apps/document-viewer && npm run build
cd src/web/apps/file-manager && npm run build
```

**Build output:** Each app produces a `dist/` folder with `index.html`, `index.js`, and `index.css` ready to be embedded in the C# host.

**esbuild-based** — see `build.mjs` in each app for configuration. TypeScript is type-checked at build time.

### C# (VSTO Add-in)

The add-in is a VSTO project (`src/DocuLink.Addin/DocuLink.Addin.csproj`). Build and run via:
- Visual Studio (recommended for debugging)
- Or MSBuild command-line if preferred

**Critical:** When you add or move a `.cs` file under `src/DocuLink.Addin/`, manually add a `<Compile Include="..."/>` entry to the `.csproj`. The project uses explicit Compile items, not automatic SDK-style globbing.

```xml
<Compile Include="Modules\Services\YourNewService.cs" />
```

Missing this entry → `type or namespace not found` at runtime.

### Python Worker

Python OCR worker is not directly invoked in development; it is launched by the C# host. To test isolated:

```bash
cd src/python
# Examine engine setup in src/python/engines/
# Worker reads NDJSON from stdin, writes NDJSON to stdout (see contracts/python-worker-v1.json)
```

## Web App Structure (TypeScript Conventions)

Both `document-viewer` and `file-manager` follow strict conventions:

### Entry Points
- `main.ts` — imports `{ mountApp }` from `./app.ts` and calls it. No logic.
- `app.ts` — exports `mountApp(root: HTMLElement): void` that wires components and mounts them.

### Components
- One folder per component, named after it
- One `.ts` file per component, sharing the folder name
- **No `index.ts` barrels** — import directly from the named file

```ts
// ✅ Correct
import { PdfViewer } from "../viewer/pdf-viewer.js";

// ❌ Wrong
import { PdfViewer } from "../viewer/index.js";
```

### Styles
- One `.css` file per component in `src/styles/`
- `main.css` is the sole entry point; import everything there
- **Never** import CSS inside `.ts` files

```css
/* main.css */
@import "@doculink/shared/base.css";
@import "./toolbar.css";
@import "./zoom-controller.css";
```

The shared package exports `base.css` for reuse.

## Backward Compatibility

**During current development:** No backward compatibility requirement. If a change breaks prior saved workbooks, that is acceptable — there are no users currently. Prioritize clean implementation over migration logic.

## No Obvious Stuff

This guide omits obvious practices (write unit tests, avoid vulnerabilities, provide helpful errors). Focus on the unique constraints:
- **Strict layering** — C#/Web/Python don't cross boundaries
- **Contracts first** — Cross-boundary shapes are versioned, not duplicated
- **Web conventions** — Named components, named imports, centralized styles
- **Explicit C# registration** — New `.cs` files need `.csproj` entries
- **File responsibility** — Search for existing homes before creating new files
