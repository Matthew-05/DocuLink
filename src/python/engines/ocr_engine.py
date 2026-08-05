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


_ENGINE_DESCRIPTION_CACHE: str | None = None

# ── Tuning knobs ──────────────────────────────────────────────────────────────
# Both settings are benchmark knobs, overridable by environment variable so the
# 2x2 matrix can be measured without rebuilding the worker:
#
#   DOCULINK_OCR_RASTERIZER   auto | pypdfium | ghostscript   (default ghostscript)
#   DOCULINK_OCR_USE_THREADS  1 | 0                           (default 1)
#
# Defaults reproduce the fastest configuration measured so far. Ghostscript wins
# today because OCRmyPDF runs page tasks in threads and its pypdfium2 plugin
# serializes every rasterization behind one process-global lock, while
# Ghostscript rasterizes out-of-process and parallelizes freely. use_threads=0
# switches OCRmyPDF to a ProcessPoolExecutor, giving each process its own pdfium
# instance — that is the configuration that could make pypdfium2 competitive.
_DEFAULT_RASTERIZER = "ghostscript"
_DEFAULT_USE_THREADS = True


def resolve_rasterizer() -> str:
    """Rasterizer to request from OCRmyPDF, honouring the environment override."""
    value = (os.environ.get("DOCULINK_OCR_RASTERIZER") or "").strip().lower()
    if value in ("auto", "pypdfium", "ghostscript"):
        return value
    return _DEFAULT_RASTERIZER


def resolve_use_threads() -> bool:
    """Whether OCRmyPDF should use threads (True) or processes (False)."""
    value = (os.environ.get("DOCULINK_OCR_USE_THREADS") or "").strip().lower()
    if value in ("0", "false", "no"):
        return False
    if value in ("1", "true", "yes"):
        return True
    return _DEFAULT_USE_THREADS


def active_rasterizer() -> str:
    """
    Report which rasterizer OCRmyPDF actually used, for diagnostics only.

    Resolves "auto" the same way ocrmypdf.builtin_plugins.pypdfium does: the
    pypdfium2 rasterizer is used whenever the package imports, and Ghostscript
    handles the page otherwise. Never branch on this value.
    """
    setting = resolve_rasterizer()
    if setting == "ghostscript":
        return "ghostscript"
    try:
        import pypdfium2  # noqa: F401
    except ImportError:
        return "ghostscript"
    return "pypdfium2"


def active_ocr_engine() -> str:
    """
    Report the OCR engine and version actually available to this worker.

    Probing the version shells out to tesseract.exe, so the result is cached for
    the lifetime of the worker process rather than paid once per document.
    """
    global _ENGINE_DESCRIPTION_CACHE
    if _ENGINE_DESCRIPTION_CACHE is not None:
        return _ENGINE_DESCRIPTION_CACHE

    description = "tesseract (version unavailable)"
    try:
        configure_tesseract()
        import pytesseract

        description = f"tesseract {pytesseract.get_tesseract_version()}"
    except Exception:  # noqa: BLE001 — diagnostics must never fail a job
        pass

    _ENGINE_DESCRIPTION_CACHE = description
    return description


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
            # See the tuning knobs above. Both are environment-overridable so the
            # rasterizer/concurrency matrix can be benchmarked without a rebuild.
            rasterizer=resolve_rasterizer(),
            use_threads=resolve_use_threads(),
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
