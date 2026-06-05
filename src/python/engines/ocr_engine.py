"""OCR engine: adds a searchable text layer to a PDF using ocrmypdf + Tesseract."""
from __future__ import annotations

import os
import sys
import tempfile
from pathlib import Path


def _configure_bundled_tools() -> None:
    """
    Locates Tesseract and Ghostscript bundled alongside the worker scripts.
    Works for both PyInstaller frozen bundles and the embeddable Python layout
    (where tools sit in the same directory as worker.py, two levels above this file).
    """
    if getattr(sys, "frozen", False):
        bundle_dir = Path(sys._MEIPASS)  # type: ignore[attr-defined]
    else:
        bundle_dir = Path(__file__).parent.parent
        if not (bundle_dir / "tesseract" / "tesseract.exe").exists():
            return

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
    """Point pytesseract at the bundled Tesseract binary."""
    if getattr(sys, "frozen", False):
        bundle_dir = Path(sys._MEIPASS)  # type: ignore[attr-defined]
    else:
        bundle_dir = Path(__file__).parent.parent
        if not (bundle_dir / "tesseract" / "tesseract.exe").exists():
            return

    tess_exe = bundle_dir / "tesseract" / "tesseract.exe"
    if tess_exe.exists():
        import pytesseract

        pytesseract.pytesseract.tesseract_cmd = str(tess_exe)


def ocr_pdf_bytes(
    pdf_bytes: bytes,
    language: str = "eng",
    auto_rotate_pages: bool = True,
    rotate_pages_threshold: float = 2.0,
    force_ocr: bool = False,
    progress_callback: "callable[[str], None] | None" = None,
) -> bytes:
    """
    Accept raw PDF bytes, run OCR, and return the new PDF bytes with an
    invisible text layer added.

    Pages that already contain selectable text are skipped (skip_text=True)
    unless force_ocr=True, which re-OCRs all pages regardless of any existing
    text layer (needed when the embedded text is missing, unextractable, or
    known to be wrong).
    When enabled, ocrmypdf uses Tesseract orientation detection to rotate pages
    that appear sideways or upside down before writing the output PDF. The
    default OCRmyPDF threshold is conservative, so use a lower value to avoid
    silently leaving clearly rotated scans uncorrected.
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

        # force_ocr and skip_text are mutually exclusive in ocrmypdf
        ocr_kwargs = {"force_ocr": True} if force_ocr else {"skip_text": True}
        ocrmypdf.ocr(
            src_path,
            dst_path,
            language=language,
            **ocr_kwargs,
            rotate_pages=auto_rotate_pages,
            rotate_pages_threshold=rotate_pages_threshold,
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
