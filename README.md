<div align="center">

<img src="src/resources/branding/doculink-icon.svg" alt="DocuLink Icon" width="134" height="154">

</div>

# DocuLink

DocuLink is an Excel add-in that bridges the gap between static PDFs and dynamic Excel data. It enables seamless PDF embedding, high-precision OCR, and deep-linking between spreadsheet cells and document regions—providing an open-source, locally-hosted alternative to enterprise document-auditing suites.

## Getting Started

DocuLink is currently in active development. 

*   **For Business/End Users:** We are working on a standard Windows installer to make setup effortless. Please keep an eye on our [Releases page](https://github.com/matthew-05/DocuLink/releases) for upcoming installer files. Once released, you will be able to install DocuLink with just a few clicks—no coding knowledge required.
*   **For Developers/Contributors:** If you'd like to test the current build, please see the **Technical & Developer Documentation** below to set up the environment and compile the add-in from source.

## Why DocuLink?

* **Accessible for All:** DocuLink is designed for every auditor and financial professional. While we provide the full source code for transparency, our upcoming distribution releases will include a standard Windows installer—no coding expertise required to get up and running.
* **Auditability & Trust:** Because DocuLink is open-source, the entire pipeline is inspectable. You control your data, and you know exactly how it is processed—there is no "black box" handling your sensitive files.
* **Local-First, Air-Gap Ready:** DocuLink runs entirely on your local machine. It requires no external cloud connections or API subscriptions, making it perfect for high-security environments where data privacy is non-negotiable.

## Key Features

* **PDF Embedding:** Store PDFs directly inside the workbook's Custom XML; no messy external file management required.
* **PDF Region Linking:** Create selection areas on PDF pages and link them to specific Excel cells. Clicking a cell jumps directly to the linked document region.
* **OCR Capabilities:** Built-in Tesseract-powered OCR turns scanned documents into searchable, data-ready content.


## Technical & Developer Documentation

This section contains information regarding the architecture, prerequisites, and build processes for contributors.

### Architecture

DocuLink spans three runtime layers that communicate via versioned contracts in `contracts/`:

| Layer | Location | Role |
| :--- | :--- | :--- |
| **C# VSTO** | `src/DocuLink.Addin/` | Excel COM integration, WebView2 host, workbook lifecycle |
| **TypeScript** | `src/web/` | Task pane and viewer UIs (document-viewer, file-manager) |
| **Python** | `src/python/` | OCR engine using Tesseract and Ghostscript, leveraging `ocrmypdf` |

Cross-boundary messages use JSON schemas defined in `contracts/`. Storage uses Custom XML parts inside the `.xlsm` workbook file.

### Prerequisites

* **Windows** with Microsoft Excel (Microsoft 365 or Excel 2019+)
* **Visual Studio** with the *Office/SharePoint development* workload
* **Node.js ≥ 18** (for building the TypeScript web apps)
* **Python ≥ 3.10** (for the OCR worker)
* **Tesseract OCR** — Download via `src/python/download-tesseract.ps1`
* **Ghostscript** — Download via `src/python/download-ghostscript.ps1`

> **Note:** Tesseract and Ghostscript binaries are not included in this repository. The provided download scripts fetch them automatically. Both are subject to their own licenses (Apache 2.0 and AGPL-3.0, respectively).

### Building

#### TypeScript (Web Apps)
```bash
cd src/web
npm install
npm run build --workspaces