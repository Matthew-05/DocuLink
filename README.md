<div align="center">

<img src="src/resources/branding/doculink-icon.svg" alt="DocuLink Icon" width="134" height="154">

</div>

# DocuLink

DocuLink is a Windows Excel VSTO add-in for embedding PDFs in workbooks, linking spreadsheet cells to document regions, and extracting text geometry from PDFs locally. It is built for audit and finance workflows where source documents need to stay attached to the workbook and remain easy to inspect.

## Getting Started

DocuLink is currently in active development.

* **For Business/End Users:** To download and install, navigate to the [Releases page](https://github.com/matthew-05/DocuLink/releases), then download and run the MSI installer when a release is available.
* **For Developers/Contributors:** If you'd like to test the current build, please see the **Technical & Developer Documentation** below to set up the environment and compile the add-in from source.

## Why DocuLink?

* **Accessible for All:** DocuLink is designed for auditors and financial professionals. While the source code is available for transparency, release builds are packaged as a standard Windows installer.
* **Auditability & Trust:** Because DocuLink is open-source, the pipeline is inspectable. You control your data and can see exactly how files are processed.
* **Local-First, Air-Gap Ready:** DocuLink runs on your local machine. OCR and PDF processing are handled locally, with no external cloud API required for normal document workflows.

## Key Features

* **PDF Embedding:** Store PDF catalog data and PDF bytes inside the workbook's Custom XML parts; no separate document folder is required for embedded files.
* **PDF Region Linking:** Create selection areas on PDF pages and link them to specific Excel cells. Clicking a linked cell can jump directly to the related document region.
* **Document Viewer:** View embedded PDFs in a WebView2 viewer with search, zoom, page controls, rotation, and rectangle editing.
* **File Manager:** Manage embedded PDFs and folders from a dedicated task-pane UI.
* **Document Matcher:** Select source and output ranges, then match document values into worksheet output columns.
* **OCR Capabilities:** Tesseract, Ghostscript, OCRmyPDF, and PyMuPDF power local OCR and text-geometry extraction through the Python worker.

## Technical & Developer Documentation

This section contains information regarding the architecture, prerequisites, and build processes for contributors.

### Architecture

DocuLink spans three runtime layers that communicate via versioned contracts in `contracts/`:

| Layer | Location | Role |
| :--- | :--- | :--- |
| **C# VSTO** | `src/DocuLink.Addin/` | Excel COM integration, WebView2 hosts, workbook lifecycle, Custom XML storage, domain services |
| **TypeScript** | `src/web/` | Task pane and viewer UIs (`document-viewer`, `file-manager`, `document-matcher`) plus shared PDF/text utilities |
| **Python** | `src/python/` | OCR and PDF text-geometry worker using Tesseract, Ghostscript, OCRmyPDF, and PyMuPDF |

Cross-boundary messages and storage formats are defined in `contracts/`:

| File | Describes |
| :--- | :--- |
| `webview-messages-v1.json` | C# to WebView2 postMessage protocol |
| `python-worker-v1.json` | C# to Python worker NDJSON protocol |
| `text-geometry-v1.json` | OCR/text layout data |
| `doculink-storage-content-v1.xsd` | PDF/folder catalogue in workbook Custom XML |
| `doculink-storage-pdf-binary-v1.xsd` | Embedded PDF binary storage in workbook Custom XML |
| `doculink-storage-links-v1.xsd` | Linked rectangles and cell references in workbook Custom XML |

Before adding or changing a cross-boundary message or storage field, update the matching contract first, then update the implementation.

### Prerequisites

* **Windows** with Microsoft Excel
* **Visual Studio** with the Office/VSTO development tooling and MSBuild
* **WebView2 Runtime**
* **Node.js + npm** for building the TypeScript web apps
* **Python 3.12+** for building the bundled OCR worker
* **WiX Toolset v3** for building the MSI installer
* **GitHub CLI (`gh`)** only if publishing releases

> **Note:** Tesseract and Ghostscript binaries are not committed to the repository. The Python worker build can download them automatically, or you can provide existing installations with `TESSERACT_DIR` and `GHOSTSCRIPT_DIR`.

### Building

#### TypeScript (Web Apps)

```powershell
cd src\web
npm install
npm run build
```

This builds all web workspaces, including `document-viewer`, `file-manager`, `document-matcher`, and shared package build steps. The C# project also runs the web build during MSBuild when web sources have changed.

#### Python Worker

```powershell
.\src\python\build-worker.ps1
```

The worker build downloads the embeddable Python runtime and required OCR tools if needed, installs `src/python/requirements.txt`, and writes the bundled worker to:

```text
src/python/dist/worker/
```

The C# build copies this folder into the add-in output automatically. If the worker has not been built, the add-in still builds, but OCR will not work until the worker is present.

#### C# VSTO Add-in

Open `src/DocuLink.Addin/DocuLink.Addin.csproj` in Visual Studio, or build with MSBuild:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
  src\DocuLink.Addin\DocuLink.Addin.csproj `
  /restore `
  /t:Build `
  /p:Configuration=Debug `
  /p:Platform=AnyCPU
```

The project targets .NET Framework 4.7.2 and uses explicit C# compile items. When adding a new `.cs` file under `src/DocuLink.Addin/`, add a matching `<Compile Include="..."/>` entry to `DocuLink.Addin.csproj`.

#### Installer

```powershell
.\installer\build-installer.ps1 -Version 1.2.0
```

The installer build performs a clean Release build, builds the Python worker, harvests the add-in output with WiX, and writes an MSI to:

```text
installer/Output/DocuLink-Setup-1.2.0.msi
```

To publish a release, use:

```powershell
.\installer\release.ps1 -Version 1.2.0
```

## License

DocuLink is licensed under the terms in [LICENSE](LICENSE). Third-party tools used for OCR and PDF processing are subject to their own licenses.
