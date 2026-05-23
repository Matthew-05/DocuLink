# -*- mode: python ; coding: utf-8 -*-
#
# PyInstaller spec for the DocuLink OCR worker.
# Target: Python 3.12, Windows x64, onedir mode.
#
# PREREQUISITES (on the build machine):
#   - Python 3.12 with packages from requirements.txt installed
#   - Tesseract: run download-tesseract.ps1 (or set TESSERACT_DIR)
#   - Ghostscript: run download-ghostscript.ps1 (or set GHOSTSCRIPT_DIR)
#
# Build:
#   pyinstaller worker.spec
#
# Output:
#   dist/worker/worker.exe  (+ supporting DLLs and tessdata)

import os
import sys
from pathlib import Path

from PyInstaller.utils.hooks import collect_data_files, collect_dynamic_libs, collect_submodules

# ── Tesseract location ────────────────────────────────────────────────────────
# Default: repo-local copy in src/python/tesseract/ (populated by download-tesseract.ps1).
# Override by setting TESSERACT_DIR env var before running pyinstaller.
_spec_dir = Path(SPECPATH)   # directory containing this .spec file
_default_tess = _spec_dir / "tesseract"

TESSERACT_DIR = Path(os.environ.get("TESSERACT_DIR", str(_default_tess)))

tess_exe = TESSERACT_DIR / "tesseract.exe"
tessdata_dir = TESSERACT_DIR / "tessdata"

if not tess_exe.exists():
    print(
        f"WARNING: Tesseract not found at {tess_exe}\n"
        "Run src/python/download-tesseract.ps1 to fetch it, or set TESSERACT_DIR.",
        file=sys.stderr,
    )

# ── Data files to bundle ──────────────────────────────────────────────────────
datas = []

if TESSERACT_DIR.is_dir():
    # Bundle tesseract.exe and every DLL it depends on (the Tesseract Windows
    # distribution ships ~70 DLLs alongside the exe; omitting them causes the
    # exe to crash immediately and ocrmypdf reports "Could not find tesseract").
    if tess_exe.exists():
        datas.append((str(tess_exe), "tesseract"))
    for dll in TESSERACT_DIR.glob("*.dll"):
        datas.append((str(dll), "tesseract"))

if tessdata_dir.is_dir():
    # Include the full tessdata folder (all language files present on build machine).
    # To include only English: bundle eng.traineddata + osd.traineddata.
    datas.append((str(tessdata_dir), "tesseract/tessdata"))

# ── Ghostscript location ──────────────────────────────────────────────────────
_default_gs = _spec_dir / "ghostscript"

GHOSTSCRIPT_DIR = Path(os.environ.get("GHOSTSCRIPT_DIR", str(_default_gs)))

gs_exe = GHOSTSCRIPT_DIR / "bin" / "gswin64c.exe"

if not gs_exe.exists():
    print(
        f"WARNING: Ghostscript not found at {gs_exe}\n"
        "Run src/python/download-ghostscript.ps1 to fetch it, or set GHOSTSCRIPT_DIR.",
        file=sys.stderr,
    )

if GHOSTSCRIPT_DIR.is_dir() and gs_exe.exists():
    # Bundle the full install tree (bin, lib, Resource, etc.).
    datas.append((str(GHOSTSCRIPT_DIR), "ghostscript"))

# ── ocrmypdf package data (fonts, ICC profiles, etc.) ────────────────────────
# ocrmypdf ships data files (e.g. Occulta.ttf) that PyInstaller won't find
# automatically via static import analysis.
datas += collect_data_files("ocrmypdf")

# ── Analysis ──────────────────────────────────────────────────────────────────
# All builtin plugins must be bundled so register_options() runs and nested
# namespaces like options.ghostscript exist (core validation accesses them).
_plugin_imports = collect_submodules("ocrmypdf.builtin_plugins")
_pdfium_binaries = collect_dynamic_libs("pypdfium2_raw")

a = Analysis(
    ["worker.py"],
    pathex=["."],
    binaries=_pdfium_binaries,
    datas=datas,
    hiddenimports=[
        "ocrmypdf",
        "ocrmypdf.builtin_plugins",
        "pypdfium2",
        "pypdfium2_raw",
        "PIL",
        "PIL.Image",
        "fitz",
        "pytesseract",
        "pytesseract.pytesseract",
        *_plugin_imports,
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="worker",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,   # stdin/stdout protocol requires console mode
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="worker",
)
