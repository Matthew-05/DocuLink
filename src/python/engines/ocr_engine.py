"""OCR engine: adds a searchable text layer to a PDF using ocrmypdf + Tesseract."""
from __future__ import annotations

import os
import re
import statistics
import sys
import tempfile
import unicodedata
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor, as_completed
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
_HOCR_BBOX_PATTERN = re.compile(
    r"(?:x_bboxes|bbox)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)"
)
_HOCR_CONFIDENCE_PATTERN = re.compile(r"x_wconf\s+(-?\d+(?:\.\d+)?)")


def summarize_geometry_quality(geometry: dict) -> dict:
    """Return cheap text-coverage signals from text-geometry-v1 data."""
    pages = geometry.get("pages", [])
    total_characters = 0
    non_whitespace_characters = 0
    alphanumeric_characters = 0
    word_count = 0
    populated_lines = 0
    page_summaries: list[dict] = []

    for page in pages:
        lines: dict[int, list[str]] = {}
        page_total_characters = 0
        page_non_whitespace_characters = 0
        page_alphanumeric_characters = 0
        page_invalid_unicode_characters = 0
        for character in page.get("characters", []):
            char = str(character.get("char", ""))
            if not char:
                continue
            page_total_characters += 1
            page_non_whitespace_characters += sum(
                not part.isspace() for part in char
            )
            page_alphanumeric_characters += sum(part.isalnum() for part in char)
            page_invalid_unicode_characters += sum(
                not part.isspace()
                and (
                    part == "\ufffd"
                    or unicodedata.category(part) in ("Cc", "Cs", "Co", "Cn")
                )
                for part in char
            )
            line_index = int(character.get("lineIndex", 0))
            lines.setdefault(line_index, []).append(char)

        page_word_count = 0
        page_populated_lines = 0
        garbled_line_count = 0
        for chars in lines.values():
            text = "".join(chars).strip()
            line_non_whitespace = sum(not char.isspace() for char in text)
            line_alphanumeric = sum(char.isalnum() for char in text)
            if (
                line_non_whitespace >= 20
                and line_alphanumeric / line_non_whitespace < 0.20
            ):
                garbled_line_count += 1
            if not any(char.isalnum() for char in text):
                continue
            page_populated_lines += 1
            page_word_count += len(text.split())

        page_alphanumeric_ratio = round(
            page_alphanumeric_characters
            / max(1, page_non_whitespace_characters),
            4,
        )
        page_invalid_unicode_ratio = round(
            page_invalid_unicode_characters
            / max(1, page_non_whitespace_characters),
            4,
        )
        page_number = int(page.get("pageIndex", len(page_summaries))) + 1
        is_garbled = (
            page_non_whitespace_characters >= 80
            and (
                page_alphanumeric_ratio < 0.30
                or page_invalid_unicode_ratio >= 0.02
                or garbled_line_count >= 3
            )
        )
        page_summaries.append(
            {
                "page_number": page_number,
                "total_characters": page_total_characters,
                "non_whitespace_characters": page_non_whitespace_characters,
                "alphanumeric_characters": page_alphanumeric_characters,
                "alphanumeric_ratio": page_alphanumeric_ratio,
                "invalid_unicode_characters": page_invalid_unicode_characters,
                "invalid_unicode_ratio": page_invalid_unicode_ratio,
                "word_count": page_word_count,
                "populated_lines": page_populated_lines,
                "garbled_line_count": garbled_line_count,
                "is_garbled": is_garbled,
            }
        )
        total_characters += page_total_characters
        non_whitespace_characters += page_non_whitespace_characters
        alphanumeric_characters += page_alphanumeric_characters
        populated_lines += page_populated_lines
        word_count += page_word_count

    garbled_page_numbers = [
        int(page["page_number"])
        for page in page_summaries
        if page["is_garbled"]
    ]
    populated_page_ratios = [
        float(page["alphanumeric_ratio"])
        for page in page_summaries
        if int(page["non_whitespace_characters"]) >= 80
    ]

    return {
        "page_count": len(pages),
        "total_characters": total_characters,
        "non_whitespace_characters": non_whitespace_characters,
        "alphanumeric_characters": alphanumeric_characters,
        "alphanumeric_ratio": round(
            alphanumeric_characters / max(1, non_whitespace_characters),
            4,
        ),
        "word_count": word_count,
        "populated_lines": populated_lines,
        "garbled_page_numbers": garbled_page_numbers,
        "garbled_page_count": len(garbled_page_numbers),
        "worst_page_alphanumeric_ratio": min(populated_page_ratios, default=1.0),
        "page_summaries": page_summaries,
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


def needs_garbled_text_retry(summary: dict) -> bool:
    """Detect page-local garbage produced by a broken PDF character map.

    Some Type0/Identity-H PDFs render perfectly but map visible glyphs to symbols
    such as ``*``, ``$`` and ``#``. Document-wide averages let healthy pages hide
    corrupt pages, so ``summarize_geometry_quality`` classifies every populated
    page independently. A retry is needed when any page is classified as garbled.
    """
    return bool(summary.get("garbled_page_numbers", []))


def select_pages_requiring_ocr(pdf_bytes: bytes, summary: dict) -> list[int]:
    """Return one-based pages whose source geometry is not trustworthy.

    Dense, plausible native text is reused directly. Sparse or garbled text is
    OCR'd, while truly blank pages are skipped unless they contain a substantial
    image or enough vector drawing content to plausibly represent outlined text.
    """
    import pymupdf as fitz

    page_summaries = {
        int(page["page_number"]): page
        for page in summary.get("page_summaries", [])
    }
    selected: list[int] = []
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    try:
        for page_index in range(doc.page_count):
            page_number = page_index + 1
            page_summary = page_summaries.get(page_number, {})
            non_whitespace = int(
                page_summary.get("non_whitespace_characters", 0)
            )
            alphanumeric_ratio = float(
                page_summary.get("alphanumeric_ratio", 0.0)
            )
            invalid_unicode_ratio = float(
                page_summary.get("invalid_unicode_ratio", 0.0)
            )
            if bool(page_summary.get("is_garbled", False)):
                selected.append(page_number)
                continue
            if (
                non_whitespace >= 80
                and alphanumeric_ratio >= 0.30
                and invalid_unicode_ratio < 0.02
            ):
                continue
            if non_whitespace > 0:
                selected.append(page_number)
                continue

            page = doc.load_page(page_index)
            page_area = page.rect.get_area()
            has_substantial_image = any(
                fitz.Rect(image.get("bbox", (0, 0, 0, 0))).get_area()
                / max(1.0, page_area)
                >= _MINIMUM_SCAN_IMAGE_COVERAGE
                for image in page.get_image_info()
            )
            has_vector_content = len(page.get_drawings()) >= 3
            if has_substantial_image or has_vector_content:
                selected.append(page_number)
    finally:
        doc.close()
    return selected


def _hocr_classes(element: ET.Element) -> set[str]:
    return set(element.attrib.get("class", "").split())


def _hocr_bbox(element: ET.Element) -> tuple[int, int, int, int] | None:
    match = _HOCR_BBOX_PATTERN.search(element.attrib.get("title", ""))
    if not match:
        return None
    x0, y0, x1, y1 = (int(value) for value in match.groups())
    if x1 <= x0 or y1 <= y0:
        return None
    return x0, y0, x1, y1


def _append_hocr_character(
    characters: list[dict],
    char: str,
    bbox: tuple[float, float, float, float],
    image_size: tuple[int, int],
    line_index: int,
) -> None:
    image_width, image_height = image_size
    x0, y0, x1, y1 = bbox
    if not char or image_width <= 0 or image_height <= 0 or x1 <= x0 or y1 <= y0:
        return
    characters.append(
        {
            "char": char,
            "x": x0 / image_width,
            "y": y0 / image_height,
            "width": (x1 - x0) / image_width,
            "height": (y1 - y0) / image_height,
            "lineIndex": line_index,
        }
    )


def _geometry_page_from_hocr(
    hocr_bytes: bytes,
    page_index: int,
    image_size: tuple[int, int],
) -> tuple[dict, dict]:
    """Convert Tesseract hOCR character boxes into one geometry page."""
    root = ET.fromstring(hocr_bytes)
    characters: list[dict] = []
    confidences: list[float] = []
    line_stats: list[dict] = []
    word_count = 0
    line_index = 0

    line_classes = {"ocr_line", "ocr_header", "ocr_caption", "ocr_textfloat"}
    for line in root.iter():
        if not (_hocr_classes(line) & line_classes):
            continue
        line_character_start = len(characters)
        line_confidences: list[float] = []
        line_word_count = 0
        previous_bbox: tuple[int, int, int, int] | None = None
        words = [
            element
            for element in line.iter()
            if "ocrx_word" in _hocr_classes(element)
        ]
        for word in words:
            word_bbox = _hocr_bbox(word)
            if word_bbox is None:
                continue
            confidence_match = _HOCR_CONFIDENCE_PATTERN.search(
                word.attrib.get("title", "")
            )
            if confidence_match:
                confidence = float(confidence_match.group(1))
                confidences.append(confidence)
                line_confidences.append(confidence)

            all_char_elements = [
                element
                for element in word.iter()
                if "ocrx_cinfo" in _hocr_classes(element)
                and "".join(element.itertext())
            ]
            if not all_char_elements or any(
                _hocr_bbox(element) is None for element in all_char_elements
            ):
                text = (
                    "".join(
                        "".join(element.itertext()).strip()
                        for element in all_char_elements
                    )
                    if all_char_elements
                    else "".join(word.itertext()).strip()
                )
                if not text:
                    continue
                x0, y0, x1, y1 = word_bbox
                width = (x1 - x0) / len(text)
                char_items = [
                    (char, (x0 + index * width, y0, x0 + (index + 1) * width, y1))
                    for index, char in enumerate(text)
                ]
            else:
                char_items = []
                for element in all_char_elements:
                    text = "".join(element.itertext()).strip()
                    bbox = _hocr_bbox(element)
                    if bbox is None or not text:
                        continue
                    x0, y0, x1, y1 = bbox
                    width = (x1 - x0) / len(text)
                    char_items.extend(
                        (
                            char,
                            (
                                x0 + index * width,
                                y0,
                                x0 + (index + 1) * width,
                                y1,
                            ),
                        )
                        for index, char in enumerate(text)
                    )
            if not char_items:
                continue

            first_bbox = char_items[0][1]
            if previous_bbox is not None:
                gap_left = float(previous_bbox[2])
                gap_right = float(first_bbox[0])
                line_height = max(float(first_bbox[3] - first_bbox[1]), 1.0)
                if gap_right <= gap_left:
                    gap_left = max(0.0, gap_right - line_height * 0.22)
                _append_hocr_character(
                    characters,
                    " ",
                    (gap_left, first_bbox[1], gap_right, first_bbox[3]),
                    image_size,
                    line_index,
                )

            for char, bbox in char_items:
                _append_hocr_character(
                    characters,
                    char,
                    bbox,
                    image_size,
                    line_index,
                )
            previous_bbox = (
                int(char_items[-1][1][0]),
                int(char_items[-1][1][1]),
                int(char_items[-1][1][2]),
                int(char_items[-1][1][3]),
            )
            word_count += 1
            line_word_count += 1
        line_text = "".join(
            str(character["char"])
            for character in characters[line_character_start:]
        ).strip()
        line_stats.append(
            {
                "line_index": line_index,
                "text": line_text,
                "word_count": line_word_count,
                "mean_confidence": round(
                    statistics.fmean(line_confidences), 2
                )
                if line_confidences
                else 0.0,
            }
        )
        line_index += 1

    return (
        {"pageIndex": page_index, "characters": characters},
        {
            "page_number": page_index + 1,
            "word_count": word_count,
            "character_count": sum(
                not str(character["char"]).isspace() for character in characters
            ),
            "mean_confidence": round(statistics.fmean(confidences), 2)
            if confidences
            else 0.0,
            "line_stats": line_stats,
        },
    )


def _direct_ocr_page(
    pdf_bytes: bytes,
    page_number: int,
    dpi: int,
    language: str,
    psm: int,
    crop_to_dominant_image: bool,
    preprocessing: str,
) -> tuple[dict, dict]:
    configure_tesseract()

    import pymupdf as fitz
    import pytesseract
    from PIL import Image

    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    try:
        page = doc.load_page(page_number - 1)
        page_rect = fitz.Rect(page.rect)
        clip = (
            _dominant_image_clip(page)
            if crop_to_dominant_image
            else None
        )
        pixmap = page.get_pixmap(dpi=dpi, alpha=False, clip=clip)
        image = Image.frombytes(
            "RGB",
            (pixmap.width, pixmap.height),
            pixmap.samples,
        )
    finally:
        doc.close()

    if preprocessing == "faint-ink":
        image = _prepare_faint_ink_image(image)
    elif preprocessing != "none":
        raise ValueError(f"Unknown direct OCR preprocessing: {preprocessing}")

    hocr_bytes = pytesseract.image_to_pdf_or_hocr(
        image,
        lang=language,
        extension="hocr",
        config=f"--psm {psm} -c hocr_char_boxes=1",
    )
    page_geometry, stats = _geometry_page_from_hocr(
        hocr_bytes,
        page_number - 1,
        image.size,
    )
    if clip is not None:
        _remap_cropped_page_geometry(page_geometry, clip, page_rect)
    stats["dpi"] = dpi
    stats["psm"] = psm
    stats["cropped"] = clip is not None
    stats["preprocessing"] = preprocessing
    return page_geometry, stats


def _prepare_faint_ink_image(image: "object") -> "object":
    """Flatten uneven scan backgrounds while retaining colored pen strokes.

    A normal grayscale conversion can make blue or red handwriting much lighter
    than black print. Taking the darkest RGB channel preserves whichever channel
    carries the most contrast for each pixel. Subtracting a blurred local
    background then suppresses shadows, colored paper, stains, and camera-lighting
    gradients without relying on one global threshold.
    """
    from PIL import ImageChops, ImageFilter, ImageOps

    red, green, blue = image.convert("RGB").split()
    darkest = ImageChops.darker(ImageChops.darker(red, green), blue)
    blur_radius = max(6, min(24, round(min(image.size) / 140)))
    background = darkest.filter(ImageFilter.GaussianBlur(blur_radius))
    local_foreground = ImageChops.subtract(background, darkest)
    return ImageOps.invert(ImageOps.autocontrast(local_foreground, cutoff=1))


def _dominant_image_clip(page: "object") -> "object | None":
    """Return an inset scan-image rectangle worth OCRing independently.

    Tesseract's automatic layout analysis can reject a legible portrait receipt
    when it is centered on a landscape PDF canvas. Cropping to the dominant scan
    removes that misleading canvas while retaining the source image itself.
    Full-page scans are left alone because clipping them cannot change layout.
    """
    import pymupdf as fitz

    page_rect = fitz.Rect(page.rect)
    page_area = page_rect.get_area()
    if page_area <= 0:
        return None

    candidates: list[fitz.Rect] = []
    for image in page.get_image_info():
        rect = fitz.Rect(image.get("bbox", (0, 0, 0, 0))) & page_rect
        if (
            rect.is_empty
            or rect.get_area() / page_area < _MINIMUM_SCAN_IMAGE_COVERAGE
        ):
            continue
        candidates.append(rect)
    if not candidates:
        return None

    dominant = max(candidates, key=lambda rect: rect.get_area())
    width_ratio = dominant.width / max(1.0, page_rect.width)
    height_ratio = dominant.height / max(1.0, page_rect.height)
    if width_ratio >= 0.98 and height_ratio >= 0.98:
        return None
    return dominant


def _remap_cropped_page_geometry(
    page_geometry: dict,
    clip: "object",
    page_rect: "object",
) -> None:
    """Map crop-normalized character boxes into original page coordinates."""
    if page_rect.width <= 0 or page_rect.height <= 0:
        return

    x_offset = (clip.x0 - page_rect.x0) / page_rect.width
    y_offset = (clip.y0 - page_rect.y0) / page_rect.height
    x_scale = clip.width / page_rect.width
    y_scale = clip.height / page_rect.height
    for character in page_geometry.get("characters", []):
        character["x"] = x_offset + float(character["x"]) * x_scale
        character["y"] = y_offset + float(character["y"]) * y_scale
        character["width"] = float(character["width"]) * x_scale
        character["height"] = float(character["height"]) * y_scale


def extract_direct_text_geometry(
    pdf_bytes: bytes,
    page_numbers: list[int],
    *,
    dpi: int = 300,
    language: str = "eng",
    psm: int = 3,
    crop_to_dominant_image: bool = False,
    preprocessing: str = "none",
    progress_callback: "callable[[str], None] | None" = None,
) -> tuple[dict[int, dict], dict[int, dict]]:
    """OCR selected pages directly to geometry without constructing a PDF."""
    if not page_numbers:
        return {}, {}

    unique_pages = sorted(set(page_numbers))
    threads_per_tesseract = 3
    previous_thread_limit = os.environ.get("OMP_THREAD_LIMIT")
    os.environ["OMP_THREAD_LIMIT"] = str(threads_per_tesseract)
    worker_count = min(
        len(unique_pages),
        max(1, (os.cpu_count() or 1) // threads_per_tesseract),
    )
    pages: dict[int, dict] = {}
    stats: dict[int, dict] = {}
    try:
        with ThreadPoolExecutor(max_workers=worker_count) as executor:
            futures = {
                executor.submit(
                    _direct_ocr_page,
                    pdf_bytes,
                    page_number,
                    dpi,
                    language,
                    psm,
                    crop_to_dominant_image,
                    preprocessing,
                ): page_number
                for page_number in unique_pages
            }
            completed = 0
            for future in as_completed(futures):
                page_number = futures[future]
                page_geometry, page_stats = future.result()
                pages[page_number] = page_geometry
                stats[page_number] = page_stats
                completed += 1
                if progress_callback:
                    progress_callback(
                        f"Direct OCR page {page_number} at {dpi} DPI "
                        f"({completed} of {len(unique_pages)})…"
                    )
    finally:
        if previous_thread_limit is None:
            os.environ.pop("OMP_THREAD_LIMIT", None)
        else:
            os.environ["OMP_THREAD_LIMIT"] = previous_thread_limit
    return pages, stats


def should_merge_faint_ink_retry(primary: dict, candidate: dict) -> bool:
    """Accept a cleanup pass only when it adds material, plausible coverage."""
    primary_characters = int(primary.get("character_count", 0))
    candidate_characters = int(candidate.get("character_count", 0))
    primary_words = int(primary.get("word_count", 0))
    candidate_words = int(candidate.get("word_count", 0))
    primary_confidence = float(primary.get("mean_confidence", 0.0))
    candidate_confidence = float(candidate.get("mean_confidence", 0.0))
    return (
        candidate_characters >= primary_characters * 1.05
        and candidate_words >= primary_words * 1.05
        and candidate_confidence >= max(45.0, primary_confidence - 15.0)
    )


def _line_groups(page: dict) -> list[list[dict]]:
    groups: dict[int, list[dict]] = {}
    order: list[int] = []
    for character in page.get("characters", []):
        line_index = int(character.get("lineIndex", 0))
        if line_index not in groups:
            groups[line_index] = []
            order.append(line_index)
        groups[line_index].append(character)
    return [groups[line_index] for line_index in order]


def _line_bounds(characters: list[dict]) -> tuple[float, float, float, float] | None:
    visible = [
        character
        for character in characters
        if str(character.get("char", "")).strip()
        and float(character.get("width", 0.0)) > 0
        and float(character.get("height", 0.0)) > 0
    ]
    if not visible:
        return None
    return (
        min(float(character["x"]) for character in visible),
        min(float(character["y"]) for character in visible),
        max(
            float(character["x"]) + float(character["width"])
            for character in visible
        ),
        max(
            float(character["y"]) + float(character["height"])
            for character in visible
        ),
    )


def _line_regions_overlap(
    first: tuple[float, float, float, float],
    second: tuple[float, float, float, float],
) -> bool:
    horizontal = max(0.0, min(first[2], second[2]) - max(first[0], second[0]))
    vertical = max(0.0, min(first[3], second[3]) - max(first[1], second[1]))
    first_width = max(1e-6, first[2] - first[0])
    second_width = max(1e-6, second[2] - second[0])
    first_height = max(1e-6, first[3] - first[1])
    second_height = max(1e-6, second[3] - second[1])
    return (
        horizontal / min(first_width, second_width) >= 0.10
        and vertical / min(first_height, second_height) >= 0.35
    )


def merge_missing_text_lines(
    primary_page: dict,
    candidate_page: dict,
    primary_line_stats: list[dict] | None = None,
    candidate_line_stats: list[dict] | None = None,
) -> tuple[dict, int, int, int]:
    """Add missing lines and replace only weak overlaps with better coverage.

    Returns the merged page, changed-line count, and word/character deltas. The
    original Tesseract reading order is retained; new lines are inserted by
    vertical position instead of globally re-sorting multi-column source text.
    """
    primary_groups = _line_groups(primary_page)
    primary_bounds = [_line_bounds(group) for group in primary_groups]
    primary_confidences = {
        int(item.get("line_index", index)): float(
            item.get("mean_confidence", 0.0)
        )
        for index, item in enumerate(primary_line_stats or [])
    }
    candidate_confidences = {
        int(item.get("line_index", index)): float(
            item.get("mean_confidence", 0.0)
        )
        for index, item in enumerate(candidate_line_stats or [])
    }
    additions: list[tuple[tuple[float, float, float, float], list[dict], str]] = []
    replacements: dict[int, tuple[list[dict], str]] = {}
    for candidate_index, group in enumerate(_line_groups(candidate_page)):
        text = "".join(str(character.get("char", "")) for character in group).strip()
        candidate_alphanumeric = sum(character.isalnum() for character in text)
        if candidate_alphanumeric < 3:
            continue
        bounds = _line_bounds(group)
        if bounds is None:
            continue
        overlapping = [
            index
            for index, occupied in enumerate(primary_bounds)
            if occupied is not None and _line_regions_overlap(bounds, occupied)
        ]
        if overlapping:
            if len(overlapping) != 1:
                continue
            primary_index = overlapping[0]
            primary_text = "".join(
                str(character.get("char", ""))
                for character in primary_groups[primary_index]
            ).strip()
            primary_alphanumeric = sum(
                character.isalnum() for character in primary_text
            )
            primary_line_index = int(
                primary_groups[primary_index][0].get("lineIndex", primary_index)
            )
            candidate_line_index = int(
                group[0].get("lineIndex", candidate_index)
            )
            primary_confidence = primary_confidences.get(
                primary_line_index, 0.0
            )
            candidate_confidence = candidate_confidences.get(
                candidate_line_index, 0.0
            )
            if not (
                primary_line_stats is not None
                and candidate_line_stats is not None
                and primary_confidence < 60.0
                and candidate_confidence >= 25.0
                and candidate_confidence >= primary_confidence - 20.0
                and candidate_alphanumeric
                >= max(primary_alphanumeric + 5, primary_alphanumeric * 1.25)
            ):
                continue
            replacements[primary_index] = (group, text)
            continue
        additions.append((bounds, group, text))

    if not additions and not replacements:
        return dict(primary_page), 0, 0, 0

    additions.sort(key=lambda item: (item[0][1], item[0][0]))
    merged_groups: list[list[dict]] = []
    addition_index = 0
    replaced_word_delta = 0
    replaced_character_delta = 0
    for primary_index, primary_group in enumerate(primary_groups):
        primary_bounds_item = _line_bounds(primary_group)
        primary_y = primary_bounds_item[1] if primary_bounds_item else 1.0
        while (
            addition_index < len(additions)
            and additions[addition_index][0][1] < primary_y
        ):
            merged_groups.append(additions[addition_index][1])
            addition_index += 1
        replacement = replacements.get(primary_index)
        if replacement is None:
            merged_groups.append(primary_group)
            continue
        replacement_group, replacement_text = replacement
        primary_text = "".join(
            str(character.get("char", "")) for character in primary_group
        ).strip()
        replaced_word_delta += len(replacement_text.split()) - len(
            primary_text.split()
        )
        replaced_character_delta += sum(
            not character.isspace() for character in replacement_text
        ) - sum(not character.isspace() for character in primary_text)
        merged_groups.append(replacement_group)
    merged_groups.extend(item[1] for item in additions[addition_index:])

    merged_characters: list[dict] = []
    for line_index, group in enumerate(merged_groups):
        for character in group:
            copied = dict(character)
            copied["lineIndex"] = line_index
            merged_characters.append(copied)

    word_delta = replaced_word_delta + sum(
        len(text.split()) for _, _, text in additions
    )
    character_delta = replaced_character_delta + sum(
        sum(not character.isspace() for character in text)
        for _, _, text in additions
    )
    merged_page = dict(primary_page)
    merged_page["characters"] = merged_characters
    return (
        merged_page,
        len(additions) + len(replacements),
        word_delta,
        character_delta,
    )


def merge_geometry_pages(
    source_geometry: dict,
    replacement_pages: dict[int, dict],
) -> dict:
    """Replace selected one-based pages while retaining trusted source geometry."""
    return {
        "version": source_geometry.get("version", 1),
        "coordinateSpace": source_geometry.get("coordinateSpace", "normalized"),
        "pages": [
            replacement_pages.get(
                int(page.get("pageIndex", index)) + 1,
                page,
            )
            for index, page in enumerate(source_geometry.get("pages", []))
        ],
    }


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


def needs_low_resolution_quality_retry(
    summary: dict,
    low_resolution_scan: bool,
) -> bool:
    """Spend on high-resolution profiles only when low DPI also yielded weak text."""
    if not low_resolution_scan:
        return False

    page_count = max(1, int(summary.get("page_count", 0)))
    return (
        int(summary.get("alphanumeric_characters", 0)) < page_count * 600
        or int(summary.get("word_count", 0)) < page_count * 80
    )


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
    pages: str | None = None,
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
            "progress_bar": False,
            # See the tuning knobs above. Both are environment-overridable so the
            # rasterizer/concurrency matrix can be benchmarked without a rebuild.
            "rasterizer": resolve_rasterizer(),
            "use_threads": resolve_use_threads(),
            "output_type": "pdf",
        }
        if auto_rotate_pages:
            options["rotate_pages_threshold"] = rotate_pages_threshold
        if tesseract_pagesegmode is not None:
            options["tesseract_pagesegmode"] = tesseract_pagesegmode
        if oversample > 0:
            options["oversample"] = oversample
        if pages:
            options["pages"] = pages

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
