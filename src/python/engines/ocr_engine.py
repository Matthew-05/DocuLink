"""OCR engine: adds a searchable text layer to a PDF using ocrmypdf + Tesseract."""
from __future__ import annotations

import os
import statistics
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


MODE_REDO = "redo"
MODE_FORCE = "force"

PROFILE_DEFAULT = "default"
PROFILE_HIGH_RESOLUTION_AUTO = "high-resolution-auto"
PROFILE_TABLE_SINGLE_BLOCK = "table-single-block"
PROFILE_TABLE_SPARSE = "table-sparse"

_ADAPTIVE_OVERSAMPLE_DPI = 300
_ADAPTIVE_PROFILE_PSMS = {
    PROFILE_HIGH_RESOLUTION_AUTO: 3,
    PROFILE_TABLE_SINGLE_BLOCK: 6,
    PROFILE_TABLE_SPARSE: 11,
}
_LOW_RESOLUTION_SCAN_DPI = 225.0
_MINIMUM_SCAN_IMAGE_COVERAGE = 0.05


def summarize_geometry_quality(geometry: dict) -> dict:
    """Return cheap text-coverage signals from text-geometry-v1 data."""
    pages = geometry.get("pages", [])
    total_characters = 0
    alphanumeric_characters = 0
    word_count = 0
    populated_lines = 0

    for page in pages:
        lines: dict[int, list[str]] = {}
        for character in page.get("characters", []):
            char = str(character.get("char", ""))
            if not char:
                continue
            total_characters += 1
            alphanumeric_characters += sum(part.isalnum() for part in char)
            line_index = int(character.get("lineIndex", 0))
            lines.setdefault(line_index, []).append(char)

        for chars in lines.values():
            text = "".join(chars).strip()
            if not any(char.isalnum() for char in text):
                continue
            populated_lines += 1
            word_count += len(text.split())

    return {
        "page_count": len(pages),
        "total_characters": total_characters,
        "alphanumeric_characters": alphanumeric_characters,
        "word_count": word_count,
        "populated_lines": populated_lines,
    }


def needs_adaptive_retry(summary: dict) -> bool:
    """Detect an implausibly sparse OCR result without penalizing blank PDFs."""
    page_count = int(summary.get("page_count", 0))
    if page_count <= 0:
        return False

    minimum_alphanumeric = max(40, page_count * 20)
    minimum_words = max(8, page_count * 4)
    minimum_lines = max(3, page_count * 2)
    return (
        int(summary.get("alphanumeric_characters", 0)) < minimum_alphanumeric
        or int(summary.get("word_count", 0)) < minimum_words
        or int(summary.get("populated_lines", 0)) < minimum_lines
    )


def needs_high_resolution_retry(pdf_bytes: bytes) -> bool:
    """Detect materially sized scan images whose effective DPI is too low for OCR."""
    import pymupdf as fitz

    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page in doc:
            page_area = page.rect.get_area()
            if page_area <= 0:
                continue
            for image in page.get_image_info(xrefs=True):
                bbox = fitz.Rect(image["bbox"])
                displayed_area = bbox.get_area()
                if displayed_area <= 0:
                    continue
                coverage = displayed_area / page_area
                if coverage < _MINIMUM_SCAN_IMAGE_COVERAGE:
                    continue
                pixel_area = int(image.get("width", 0)) * int(image.get("height", 0))
                if pixel_area <= 0:
                    continue
                effective_dpi = 72.0 * (pixel_area / displayed_area) ** 0.5
                if effective_dpi < _LOW_RESOLUTION_SCAN_DPI:
                    return True
        return False
    finally:
        doc.close()


def profile_ocr_options(profile: str) -> dict:
    """Translate an internal OCR profile into OCRmyPDF/Tesseract options."""
    if profile == PROFILE_DEFAULT:
        return {"tesseract_pagesegmode": None, "oversample": 0}
    if profile not in _ADAPTIVE_PROFILE_PSMS:
        raise ValueError(f"Unknown OCR profile: {profile}")
    return {
        "tesseract_pagesegmode": _ADAPTIVE_PROFILE_PSMS[profile],
        "oversample": _ADAPTIVE_OVERSAMPLE_DPI,
    }


def select_best_adaptive_profile(evaluations: list[dict]) -> dict:
    """Prefer confidence and recall while giving coherent PSM 6 ordering a tie-break."""
    if not evaluations:
        raise ValueError("No adaptive OCR profiles were evaluated")

    def score(evaluation: dict) -> float:
        confidence = float(evaluation.get("mean_confidence", 0.0))
        word_count = int(evaluation.get("word_count", 0))
        order_bonus = (
            3.0
            if evaluation.get("profile")
            in {PROFILE_HIGH_RESOLUTION_AUTO, PROFILE_TABLE_SINGLE_BLOCK}
            else 0.0
        )
        return confidence + min(12.0, word_count / 20.0) + order_bonus

    return max(evaluations, key=score)


def evaluate_adaptive_profiles(pdf_bytes: bytes, language: str = "eng") -> list[dict]:
    """
    Score high-resolution Tesseract layouts at 300 DPI before committing to a retry.

    OCRmyPDF's final PDF does not retain word confidence, so this lightweight
    preflight renders each source page once and asks Tesseract for TSV-equivalent
    data for automatic, single-block, and sparse layouts. The chosen profile is
    then run through OCRmyPDF so
    text placement and PDF preservation remain in the existing engine.
    """
    configure_tesseract()

    import pymupdf as fitz
    import pytesseract
    from PIL import Image

    aggregates = {
        profile: {"confidences": [], "words": [], "lines": set()}
        for profile in _ADAPTIVE_PROFILE_PSMS
    }
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page_index in range(doc.page_count):
            page = doc.load_page(page_index)
            pixmap = page.get_pixmap(dpi=_ADAPTIVE_OVERSAMPLE_DPI, alpha=False)
            image = Image.frombytes(
                "RGB",
                (pixmap.width, pixmap.height),
                pixmap.samples,
            )

            for profile, psm in _ADAPTIVE_PROFILE_PSMS.items():
                data = pytesseract.image_to_data(
                    image,
                    lang=language,
                    config=f"--psm {psm}",
                    output_type=pytesseract.Output.DICT,
                )
                aggregate = aggregates[profile]
                for index, raw_text in enumerate(data.get("text", [])):
                    text = str(raw_text).strip()
                    if not text:
                        continue
                    try:
                        confidence = float(data["conf"][index])
                    except (KeyError, TypeError, ValueError, IndexError):
                        continue
                    if confidence < 0:
                        continue
                    aggregate["confidences"].append(confidence)
                    aggregate["words"].append(text)
                    aggregate["lines"].add(
                        (
                            page_index,
                            data.get("block_num", [0])[index],
                            data.get("par_num", [0])[index],
                            data.get("line_num", [0])[index],
                        )
                    )
    finally:
        doc.close()

    evaluations: list[dict] = []
    for profile, aggregate in aggregates.items():
        confidences = aggregate["confidences"]
        words = aggregate["words"]
        evaluations.append(
            {
                "profile": profile,
                "mean_confidence": round(statistics.fmean(confidences), 2)
                if confidences
                else 0.0,
                "median_confidence": round(statistics.median(confidences), 2)
                if confidences
                else 0.0,
                "word_count": len(words),
                "alphanumeric_characters": sum(
                    sum(char.isalnum() for char in word) for word in words
                ),
                "populated_lines": len(aggregate["lines"]),
            }
        )
    return evaluations


def ocr_pdf_bytes(
    pdf_bytes: bytes,
    language: str = "eng",
    auto_rotate_pages: bool = True,
    rotate_pages_threshold: float = 2.0,
    mode: str = MODE_REDO,
    tesseract_pagesegmode: int | None = None,
    oversample: int = 0,
    progress_callback: "callable[[str], None] | None" = None,
) -> bytes:
    """
    Accept raw PDF bytes, run OCR, and return the new PDF bytes with a fresh
    invisible text layer.

    mode selects OCRmyPDF's processing mode and is the difference between
    preserving and destroying the document:

      MODE_REDO  — strips the existing invisible text layer and OCRs the image
                   regions, leaving original page content untouched. Vector text
                   stays vector, so the viewer keeps full zoom fidelity and the
                   stored PDF does not balloon. This is the correct default.
      MODE_FORCE — rasterizes every page at a fixed DPI and OCRs the bitmap.
                   Permanently discards vector content and inflates the file, so
                   it is only reached by escalation from the ladder in worker.py
                   when redo cannot produce a usable result.

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

        options = {
            "language": language,
            "mode": mode,
            "rotate_pages": auto_rotate_pages,
            "rotate_pages_threshold": rotate_pages_threshold,
            "progress_bar": False,
            # See the tuning knobs above. Both are environment-overridable so the
            # rasterizer/concurrency matrix can be benchmarked without a rebuild.
            "rasterizer": resolve_rasterizer(),
            "use_threads": resolve_use_threads(),
            "output_type": "pdf",
        }
        if tesseract_pagesegmode is not None:
            options["tesseract_pagesegmode"] = tesseract_pagesegmode
        if oversample > 0:
            options["oversample"] = oversample

        ocrmypdf.ocr(src_path, dst_path, **options)

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
