"""Convert non-PDF source documents into PDFs (or into HTML for the host to render).

This engine covers the formats that can be handled with libraries already bundled
in the worker (Pillow and PyMuPDF) plus the Python standard library:

  • raster images        → Pillow, one page per frame
  • plain text / markdown→ PyMuPDF Story layout
  • svg/epub/xps/fb2/cbz → PyMuPDF document open + convert_to_pdf
  • eml / mht / mhtml    → parsed to a self-contained HTML document
  • spreadsheets         → styled HTML, see engines/spreadsheet_engine.py

Word, PowerPoint and Outlook formats (.docx, .pptx, .msg, …) are deliberately NOT
handled here. The C# host converts those through late-bound Office automation,
which has far better fidelity and requires no extra bundled runtime. See
Modules/Services/Conversion/OfficeInteropConverter.cs.

Spreadsheets used to be in that group and are not any more. Excel cannot be
automated from inside Excel without borrowing the user's own instance — DocuLink
is loaded into it, so every activation route returns the host — which meant each
converted workbook opened a visible window in their session. Attempts to isolate
it in a private process failed on Excel's own single-instance behaviour: it hands
the command line off to the running copy, so the bootstrap workbook surfaced in
the user's Excel anyway. Reading the file here costs print fidelity and buys a
conversion that never touches the user's session.

Conversion results are described by contracts/python-worker-v1.json:
a result is either finished PDF bytes ("pdf") or an HTML document that the host
renders to PDF itself ("html").
"""
from __future__ import annotations

import base64
import html as html_module
import io
import mimetypes
import re
from email import policy
from email.parser import BytesParser
from typing import Callable

import fitz
from PIL import Image, ImageSequence

from engines.spreadsheet_engine import (
    SPREADSHEET_EXTENSIONS,
    SpreadsheetError,
    spreadsheet_to_html,
)

# ── Format routing ────────────────────────────────────────────────────────────
# Keep these sets in sync with ConversionFormatCatalog.cs on the C# side; the
# host decides which converter to call, this engine only guards against being
# handed something it cannot process.

IMAGE_EXTENSIONS = frozenset(
    {".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".dib",
     ".tif", ".tiff", ".webp", ".ico", ".ppm", ".pgm", ".pnm", ".jp2"}
)

TEXT_EXTENSIONS = frozenset({".txt", ".md", ".markdown", ".log"})

# Document formats MuPDF can open directly and re-emit as PDF.
MUPDF_EXTENSIONS = frozenset({".svg", ".epub", ".xps", ".oxps", ".fb2", ".cbz", ".mobi"})

MAIL_EXTENSIONS = frozenset({".eml", ".mht", ".mhtml"})

SUPPORTED_EXTENSIONS = (
    IMAGE_EXTENSIONS
    | TEXT_EXTENSIONS
    | MUPDF_EXTENSIONS
    | MAIL_EXTENSIONS
    | SPREADSHEET_EXTENSIONS
)

# MuPDF wants a filetype hint without the leading dot; a couple of names differ.
_MUPDF_FILETYPES = {".oxps": "xps"}

_PAGE_SIZE = "letter"
_PAGE_MARGIN = 54.0  # 0.75in, in PDF points


class ConversionError(Exception):
    """Raised when a source document cannot be converted."""


class ConversionOutput:
    """Either finished PDF bytes or an HTML document for the host to render."""

    __slots__ = ("kind", "pdf_bytes", "html")

    def __init__(self, kind: str, pdf_bytes: bytes = b"", html: str = "") -> None:
        self.kind = kind
        self.pdf_bytes = pdf_bytes
        self.html = html

    @staticmethod
    def pdf(data: bytes) -> "ConversionOutput":
        return ConversionOutput("pdf", pdf_bytes=data)

    @staticmethod
    def as_html(markup: str) -> "ConversionOutput":
        return ConversionOutput("html", html=markup)


def convert_to_pdf(
    source_bytes: bytes,
    extension: str,
    source_name: str = "",
    progress_callback: Callable[[str], None] | None = None,
) -> ConversionOutput:
    """Converts a source document to PDF bytes, or to HTML when only the host can render it."""
    ext = _normalise_extension(extension)

    def progress(message: str) -> None:
        if progress_callback:
            progress_callback(message)

    if not source_bytes:
        raise ConversionError("Source document is empty.")

    if ext in IMAGE_EXTENSIONS:
        progress("Converting image…")
        return ConversionOutput.pdf(_image_to_pdf(source_bytes))

    if ext in TEXT_EXTENSIONS:
        progress("Laying out text…")
        return ConversionOutput.pdf(_text_to_pdf(source_bytes, ext, source_name))

    if ext in MUPDF_EXTENSIONS:
        progress("Converting document…")
        return ConversionOutput.pdf(_mupdf_to_pdf(source_bytes, ext))

    if ext in MAIL_EXTENSIONS:
        progress("Reading message…")
        return ConversionOutput.as_html(_mail_to_html(source_bytes, source_name))

    if ext in SPREADSHEET_EXTENSIONS:
        progress("Reading spreadsheet…")
        try:
            return ConversionOutput.as_html(
                spreadsheet_to_html(source_bytes, ext, source_name)
            )
        except SpreadsheetError as exc:
            # Surfaced per-file by the host, so it has to read as an explanation
            # rather than a stack trace.
            raise ConversionError(str(exc)) from exc

    raise ConversionError(f"Unsupported source type '{ext}'.")


def _normalise_extension(extension: str) -> str:
    ext = (extension or "").strip().lower()
    if ext and not ext.startswith("."):
        ext = "." + ext
    return ext


# ── Images ────────────────────────────────────────────────────────────────────

def _image_to_pdf(source_bytes: bytes) -> bytes:
    """Renders every frame of a raster image to its own PDF page."""
    try:
        with Image.open(io.BytesIO(source_bytes)) as image:
            frames = [
                _flatten_frame(frame)
                for frame in ImageSequence.Iterator(image)
            ]

            if not frames:
                raise ConversionError("Image contains no frames.")

            buffer = io.BytesIO()
            frames[0].save(
                buffer,
                format="PDF",
                resolution=_image_resolution(image),
                save_all=len(frames) > 1,
                append_images=frames[1:],
            )
            return buffer.getvalue()
    except ConversionError:
        raise
    except Exception as exc:  # noqa: BLE001
        raise ConversionError(f"Could not convert image: {exc}") from exc


def _flatten_frame(frame: Image.Image) -> Image.Image:
    """Copies a frame to RGB, compositing transparency onto white.

    PDF has no alpha channel for the simple image-per-page encoding Pillow uses,
    so an un-flattened RGBA frame would render with a black background.
    """
    converted = frame.convert("RGBA") if frame.mode in ("RGBA", "LA", "PA") else frame.convert("RGB")
    if converted.mode == "RGBA":
        background = Image.new("RGB", converted.size, (255, 255, 255))
        background.paste(converted, mask=converted.split()[-1])
        return background
    return converted.copy()


def _image_resolution(image: Image.Image) -> float:
    """Uses the image's own DPI when it declares one, else assumes 96 dpi."""
    dpi = image.info.get("dpi")
    if isinstance(dpi, (tuple, list)) and dpi and isinstance(dpi[0], (int, float)) and dpi[0] > 1:
        return float(dpi[0])
    return 96.0


# ── Text and markdown ─────────────────────────────────────────────────────────

_TEXT_CSS = """
body { font-family: sans-serif; font-size: 10pt; line-height: 1.45; }
pre { font-family: monospace; font-size: 9pt; white-space: pre-wrap; }
h1 { font-size: 16pt; } h2 { font-size: 13pt; } h3 { font-size: 11pt; }
"""


def _text_to_pdf(source_bytes: bytes, ext: str, source_name: str) -> bytes:
    text = _decode_text(source_bytes)
    if ext in (".md", ".markdown"):
        body = _markdown_to_html(text)
    else:
        body = "<pre>" + html_module.escape(text) + "</pre>"

    title = html_module.escape(source_name or "Document")
    markup = f"<html><head><title>{title}</title></head><body>{body}</body></html>"
    return _html_to_pdf_via_story(markup, _TEXT_CSS)


def _decode_text(source_bytes: bytes) -> str:
    """Decodes text using the BOM when present, then UTF-8, then Latin-1 as a last resort."""
    for bom, encoding in (
        (b"\xef\xbb\xbf", "utf-8-sig"),
        (b"\xff\xfe", "utf-16"),
        (b"\xfe\xff", "utf-16"),
    ):
        if source_bytes.startswith(bom):
            try:
                return source_bytes.decode(encoding)
            except UnicodeDecodeError:
                break
    try:
        return source_bytes.decode("utf-8")
    except UnicodeDecodeError:
        return source_bytes.decode("latin-1", errors="replace")


def _markdown_to_html(text: str) -> str:
    """Minimal markdown rendering — headings, bullets and paragraphs.

    A full markdown parser is not bundled; this covers the structure people
    actually notice in a converted note without adding a dependency.
    """
    lines = text.splitlines()
    out: list[str] = []
    in_list = False

    def close_list() -> None:
        nonlocal in_list
        if in_list:
            out.append("</ul>")
            in_list = False

    for raw_line in lines:
        line = raw_line.rstrip()
        heading = re.match(r"^(#{1,6})\s+(.*)$", line)
        bullet = re.match(r"^\s*[-*+]\s+(.*)$", line)

        if heading:
            close_list()
            level = min(len(heading.group(1)), 6)
            out.append(f"<h{level}>{_inline_markdown(heading.group(2))}</h{level}>")
        elif bullet:
            if not in_list:
                out.append("<ul>")
                in_list = True
            out.append(f"<li>{_inline_markdown(bullet.group(1))}</li>")
        elif not line.strip():
            close_list()
        else:
            close_list()
            out.append(f"<p>{_inline_markdown(line)}</p>")

    close_list()
    return "".join(out)


def _inline_markdown(text: str) -> str:
    escaped = html_module.escape(text)
    escaped = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", escaped)
    escaped = re.sub(r"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", r"<i>\1</i>", escaped)
    escaped = re.sub(r"`(.+?)`", r"<code>\1</code>", escaped)
    return escaped


def _html_to_pdf_via_story(markup: str, css: str) -> bytes:
    """Paginates an HTML fragment with PyMuPDF's Story layout engine."""
    try:
        buffer = io.BytesIO()
        story = fitz.Story(html=markup, user_css=css)
        writer = fitz.DocumentWriter(buffer)
        mediabox = fitz.paper_rect(_PAGE_SIZE)
        where = mediabox + (_PAGE_MARGIN, _PAGE_MARGIN, -_PAGE_MARGIN, -_PAGE_MARGIN)

        more = True
        guard = 0
        while more:
            guard += 1
            if guard > 5000:
                raise ConversionError("Text layout did not terminate.")
            device = writer.begin_page(mediabox)
            more, _ = story.place(where)
            story.draw(device)
            writer.end_page()

        writer.close()
        return _compress_pdf(buffer.getvalue())
    except ConversionError:
        raise
    except Exception as exc:  # noqa: BLE001
        raise ConversionError(f"Could not lay out text: {exc}") from exc


# ── MuPDF-native document formats ─────────────────────────────────────────────

def _mupdf_to_pdf(source_bytes: bytes, ext: str) -> bytes:
    filetype = _MUPDF_FILETYPES.get(ext, ext.lstrip("."))
    try:
        with fitz.open(stream=source_bytes, filetype=filetype) as document:
            if document.page_count == 0:
                raise ConversionError("Document contains no pages.")
            if document.is_pdf:
                return _compress_pdf(document.tobytes())
            return _compress_pdf(document.convert_to_pdf())
    except ConversionError:
        raise
    except Exception as exc:  # noqa: BLE001
        raise ConversionError(f"Could not convert {filetype} document: {exc}") from exc


# ── Output compression ────────────────────────────────────────────────────────


def _compress_pdf(pdf_bytes: bytes) -> bytes:
    """Re-saves a PDF with deflated streams, subset fonts and no orphaned objects.

    MuPDF writes uncompressed content streams by default, from both DocumentWriter
    and convert_to_pdf, and the difference is not marginal: a text document comes
    out around thirty times larger than it needs to be, an epub around twice.

    That matters more here than it would in a normal converter, because the host
    base64-encodes the result into the workbook's Custom XML — so every byte
    costs four thirds of a byte in a file the user has to save, sync and reopen.
    A 3.6MB text PDF was putting nearly 5MB into a workbook for about 120KB of
    actual content.

    Not applied to the image path: Pillow already emits compressed image streams,
    so a second pass measurably costs time and returns nothing.

    Best-effort throughout. A PDF that cannot be re-saved, or one that somehow
    grows, is passed through untouched — shipping a large PDF beats failing a
    conversion that had already succeeded.
    """
    if not pdf_bytes:
        return pdf_bytes

    try:
        with fitz.open("pdf", pdf_bytes) as document:
            # Embedded fonts are the bulk of a text PDF and MuPDF embeds them
            # whole. Not available on every PyMuPDF build, and not worth failing
            # the compression pass over.
            try:
                document.subset_fonts()
            except Exception:  # noqa: BLE001
                pass

            compressed = document.tobytes(
                garbage=4,          # merge duplicate objects and drop unreachable ones
                deflate=True,
                deflate_images=True,
                deflate_fonts=True,
                clean=True,
            )

        return compressed if 0 < len(compressed) < len(pdf_bytes) else pdf_bytes
    except Exception:  # noqa: BLE001
        return pdf_bytes


# ── Email messages ────────────────────────────────────────────────────────────

_MAIL_CSS = """
body { font-family: 'Segoe UI', sans-serif; font-size: 11pt; margin: 24px; color: #111; }
table.doculink-headers { border-collapse: collapse; margin-bottom: 18px; width: 100%; }
table.doculink-headers th { text-align: left; padding: 2px 12px 2px 0; width: 90px;
    vertical-align: top; color: #555; font-weight: 600; }
table.doculink-headers td { padding: 2px 0; vertical-align: top; }
hr.doculink-rule { border: none; border-top: 1px solid #ccc; margin: 0 0 18px 0; }
pre.doculink-body { font-family: 'Segoe UI', sans-serif; white-space: pre-wrap; font-size: 11pt; }
img { max-width: 100%; }
"""

_HEADER_FIELDS = ("From", "To", "Cc", "Bcc", "Date", "Subject")


def _mail_to_html(source_bytes: bytes, source_name: str) -> str:
    """Renders an .eml/.mhtml message as a self-contained HTML document.

    Inline (cid:) images are inlined as data URIs because the host renders the
    result from a temp file with no access to the original MIME parts.
    """
    try:
        message = BytesParser(policy=policy.default).parsebytes(source_bytes)
    except Exception as exc:  # noqa: BLE001
        raise ConversionError(f"Could not parse message: {exc}") from exc

    header_rows = []
    for field in _HEADER_FIELDS:
        value = message.get(field)
        if not value:
            continue
        header_rows.append(
            f"<tr><th>{html_module.escape(field)}</th>"
            f"<td>{html_module.escape(str(value))}</td></tr>"
        )

    body_html = _mail_body_html(message)
    body_html = _inline_cid_images(body_html, message)

    title = html_module.escape(str(message.get("Subject") or source_name or "Message"))
    headers = (
        f"<table class='doculink-headers'>{''.join(header_rows)}</table><hr class='doculink-rule'/>"
        if header_rows
        else ""
    )
    attachments = _attachment_list_html(message)

    return (
        "<!DOCTYPE html><html><head><meta charset='utf-8'/>"
        f"<title>{title}</title><style>{_MAIL_CSS}</style></head>"
        f"<body>{headers}{body_html}{attachments}</body></html>"
    )


def _mail_body_html(message) -> str:
    """Prefers the HTML alternative, falling back to escaped plain text."""
    try:
        html_part = message.get_body(preferencelist=("html",))
    except Exception:  # noqa: BLE001
        html_part = None

    if html_part is not None:
        try:
            return html_part.get_content()
        except Exception:  # noqa: BLE001
            pass

    try:
        text_part = message.get_body(preferencelist=("plain",))
        if text_part is not None:
            return "<pre class='doculink-body'>" + html_module.escape(text_part.get_content()) + "</pre>"
    except Exception:  # noqa: BLE001
        pass

    return "<pre class='doculink-body'>(No readable message body.)</pre>"


def _inline_cid_images(body_html: str, message) -> str:
    """Replaces cid: references with data URIs for the message's inline parts."""
    replacements: dict[str, str] = {}

    for part in message.walk():
        content_id = part.get("Content-ID")
        if not content_id:
            continue
        key = content_id.strip().strip("<>")
        try:
            payload = part.get_payload(decode=True)
        except Exception:  # noqa: BLE001
            payload = None
        if not payload:
            continue
        mime = part.get_content_type() or "application/octet-stream"
        replacements[key] = f"data:{mime};base64,{base64.b64encode(payload).decode('ascii')}"

    if not replacements:
        return body_html

    def substitute(match: "re.Match[str]") -> str:
        key = match.group(2)
        return match.group(1) + replacements.get(key, "cid:" + key) + match.group(3)

    return re.sub(
        r"(['\"])cid:([^'\"]+)(['\"])",
        lambda m: substitute(m),
        body_html,
        flags=re.IGNORECASE,
    )


def _attachment_list_html(message) -> str:
    """Lists non-inline attachment names; their contents are not embedded."""
    names = []
    for part in message.walk():
        if part.get_content_disposition() != "attachment":
            continue
        name = part.get_filename()
        if name:
            names.append(html_module.escape(name))

    if not names:
        return ""

    items = "".join(f"<li>{name}</li>" for name in names)
    return (
        "<hr class='doculink-rule'/><p><b>Attachments</b> "
        "(not included in the converted PDF):</p>"
        f"<ul>{items}</ul>"
    )


# Ensure the mimetypes table is initialised for attachment naming on minimal images.
mimetypes.init()
