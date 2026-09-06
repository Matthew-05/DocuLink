# -*- mode: python ; coding: utf-8 -*-
#
# PyInstaller spec for the Talliark OCR worker.
# Target: Python 3.12, Windows x64, onedir mode.
#
# PREREQUISITES (on the build machine):
#   - Python 3.12 with packages from requirements.txt installed
#   - Tesseract: run download-tesseract.ps1 (or set TESSERACT_DIR)
#
# Build:
#   pyinstaller worker.spec
#
# Output:
#   dist/worker/worker.exe  (+ supporting DLLs and tessdata)

import os
import sys
from pathlib import Path

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
    # exe to crash immediately when direct OCR invokes it).
    if tess_exe.exists():
        datas.append((str(tess_exe), "tesseract"))
    for dll in TESSERACT_DIR.glob("*.dll"):
        datas.append((str(dll), "tesseract"))

if tessdata_dir.is_dir():
    # Include the full tessdata folder (all language files present on build machine).
    # To include only English: bundle eng.traineddata + osd.traineddata.
    datas.append((str(tessdata_dir), "tesseract/tessdata"))

# ── Analysis ──────────────────────────────────────────────────────────────────
a = Analysis(
    ["worker.py"],
    pathex=["."],
    binaries=[],
    datas=datas,
    hiddenimports=[
        "PIL",
        "PIL.Image",
        "PIL.ImageSequence",
        "PIL.PdfImagePlugin",   # Pillow's PDF writer, used by the conversion engine
        "fitz",
        "pytesseract",
        "pytesseract.pytesseract",
        # Spreadsheet readers. Each is reached through a local import inside
        # engines/spreadsheet_engine.py, so PyInstaller's static analysis does
        # not find them on its own. (.ods needs nothing here — it is read with
        # zipfile and ElementTree.)
        "openpyxl",
        "openpyxl.utils",
        "xlrd",
        "pyxlsb",
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
