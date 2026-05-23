"""OCR engine: adds a searchable text layer to a PDF using ocrmypdf + Tesseract."""
from __future__ import annotations

import os
import sys
import tempfile
from pathlib import Path


def _configure_bundled_tools() -> None:
    """
    When running as a PyInstaller bundle, Tesseract and Ghostscript live under
    bundled subdirectories. ocrmypdf locates both via shutil.which; patch that
    lookup and prepend each tool's bin directory to PATH so sibling DLLs load.
    """
    if not getattr(sys, "frozen", False):
        return

    # sys._MEIPASS is the resource root (PyInstaller 6+ uses _internal/).
    bundle_dir = Path(sys._MEIPASS)  # type: ignore[attr-defined]

    paths_to_prepend: list[Path] = []
    which_overrides: dict[str, str] = {}

    tess_exe = bundle_dir / "tesseract" / "tesseract.exe"
    if tess_exe.exists():
        paths_to_prepend.append(tess_exe.parent)
        which_overrides["tesseract"] = str(tess_exe)
        tessdata = bundle_dir / "tesseract" / "tessdata"
        if tessdata.is_dir():
            os.environ.setdefault("TESSDATA_PREFIX", str(tessdata))

    gs_exe = bundle_dir / "ghostscript" / "bin" / "gswin64c.exe"
    if gs_exe.exists():
        paths_to_prepend.append(gs_exe.parent)
        which_overrides["gswin64c"] = str(gs_exe)

    if not paths_to_prepend:
        return

    os.environ["PATH"] = (
        os.pathsep.join(str(p) for p in paths_to_prepend)
        + os.pathsep
        + os.environ.get("PATH", "")
    )

    if which_overrides:
        import shutil as _shutil

        _real_which = _shutil.which

        def _which_patched(name, mode=os.F_OK | os.X_OK, path=None):
            if name in which_overrides:
                return which_overrides[name]
            return _real_which(name, mode=mode, path=path)

        _shutil.which = _which_patched


_configure_bundled_tools()

import ocrmypdf  # noqa: E402 — must come after env setup


def configure_tesseract() -> None:
    """Point pytesseract at the bundled Tesseract binary when frozen."""
    if not getattr(sys, "frozen", False):
        return

    bundle_dir = Path(sys._MEIPASS)  # type: ignore[attr-defined]
    tess_exe = bundle_dir / "tesseract" / "tesseract.exe"
    if tess_exe.exists():
        import pytesseract

        pytesseract.pytesseract.tesseract_cmd = str(tess_exe)


def ocr_pdf_bytes(
    pdf_bytes: bytes,
    language: str = "eng",
    progress_callback: "callable[[str], None] | None" = None,
) -> bytes:
    """
    Accept raw PDF bytes, run OCR, and return the new PDF bytes with an
    invisible text layer added.

    Pages that already contain selectable text are skipped (skip_text=True).
    Raises ocrmypdf.exceptions.OcrmypdfException on failure.
    """
    with tempfile.NamedTemporaryFile(suffix=".pdf", delete=False) as src_f:
        src_path = src_f.name
        src_f.write(pdf_bytes)

    dst_fd, dst_path = tempfile.mkstemp(suffix=".pdf")
    os.close(dst_fd)

    try:
        if progress_callback:
            progress_callback("Starting OCR…")

        ocrmypdf.ocr(
            src_path,
            dst_path,
            language=language,
            skip_text=True,
            progress_bar=False,
            rasterizer="ghostscript",
            output_type="pdf",
        )

        if progress_callback:
            progress_callback("OCR complete, reading output…")

        with open(dst_path, "rb") as f:
            return f.read()
    finally:
        try:
            os.unlink(src_path)
        except OSError:
            pass
        try:
            os.unlink(dst_path)
        except OSError:
            pass
