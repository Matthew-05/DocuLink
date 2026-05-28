# DocuLink

An Excel VSTO add-in that embeds PDFs directly into workbooks, OCR capabilities, and linking between spreadsheet cells and document regions.

## Features

- **PDF embedding** — store PDFs inside the workbook's Custom XML; no external files required
- **PDF Region Linking** — create selection areas on PDF pages and link them to Excel cells; clicking a cell jumps to the linked region
- **OCR** — Tesseract-powered OCR.

## Architecture

DocuLink spans three runtime layers that communicate via versioned contracts in [`contracts/`](contracts/):

| Layer | Location | Role |
|-------|----------|------|
| **C# VSTO** | `src/DocuLink.Addin/` | Excel COM integration, WebView2 host, workbook lifecycle |
| **TypeScript** | `src/web/` | Task pane and viewer UIs (document-viewer, file-manager) |
| **Python** | `src/python/` | OCR engine using Tesseract and Ghostscript, leveraging ocrmypdf  |

Cross-boundary messages use JSON schemas defined in `contracts/`. Storage uses Custom XML parts inside the `.xlsm` workbook file.

## Prerequisites

- **Windows** with Microsoft Excel (Microsoft 365 or Excel 2019+)
- **Visual Studio** with the *Office/SharePoint development* workload
- **Node.js ≥ 18** (for building the TypeScript web apps)
- **Python ≥ 3.10** (for the OCR worker)
- **Tesseract OCR** — download via `src/python/download-tesseract.ps1`
- **Ghostscript** — download via `src/python/download-ghostscript.ps1`

> Tesseract and Ghostscript binaries are not included in this repository. The download scripts fetch them automatically and place them in `src/python/tesseract/` and `src/python/ghostscript/`. Both are subject to their own licenses (Apache 2.0 and AGPL-3.0, respectively).

## Building

### TypeScript (web apps)

```bash
cd src/web
npm install
npm run build --workspaces
```

Build output lands in `src/web/apps/document-viewer/dist/` and `src/web/apps/file-manager/dist/`.

### C# add-in

Open `src/DocuLink.Addin/DocuLink.Addin.sln` in Visual Studio and build. The VSTO add-in registers itself with Excel on first debug run.

### Python OCR worker

```powershell
# Build the standalone worker executable
src/python/build-worker.ps1

# Or individually download tesseract and ghostscript first
src/python/download-tesseract.ps1
src/python/download-ghostscript.ps1

```

The worker is launched as a subprocess by the C# host and communicates via NDJSON on stdin/stdout (see [`contracts/python-worker-v1.json`](contracts/python-worker-v1.json)).

## Development

The TypeScript web apps are embedded HTML/JS bundles served by WebView2 inside the VSTO host. The C# layer routes `postMessage` events between Excel and the web UI — see [`contracts/webview-messages-v1.json`](contracts/webview-messages-v1.json) for the full message protocol.

## License

DocuLink source code is licensed under the [Mozilla Public License 2.0](LICENSE).

Third-party dependencies have their own licenses. Notable ones to be aware of when using this software:

- **Ghostscript** (AGPL-3.0) — downloaded separately; not bundled in this repository
- **Tesseract OCR** (Apache 2.0) — downloaded separately; not bundled in this repository
- **pdf.js** (Apache 2.0) — bundled via npm
- **ocrmypdf** (MPL-2.0) — Python dependency
