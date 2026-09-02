"""Normalize PDFs into passive workbook-safe reference documents."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass

import pymupdf


@dataclass(frozen=True)
class PdfSanitizationResult:
    pdf_bytes: bytes
    removed_embedded_files: int
    removed_links: int


def sanitize_pdf_bytes(
    pdf_bytes: bytes,
    progress_callback: Callable[[str], None] | None = None,
) -> PdfSanitizationResult:
    """
    Rewrite a PDF while removing content that can perform actions or carry files.

    DocuLink's viewer is a reference surface, so JavaScript, attachments, launch
    actions, links, thumbnails, response data, and XML metadata are unnecessary.
    Visible page content and hidden OCR text are retained.
    """
    if not pdf_bytes:
        raise ValueError("PDF is empty.")

    document = pymupdf.open(stream=pdf_bytes, filetype="pdf")
    try:
        embedded_files = document.embfile_count()
        links = 0
        page_count = document.page_count
        for page_index, page in enumerate(document):
            links += len(page.get_links())
            if progress_callback:
                progress_callback(
                    f"Securing PDF page {page_index + 1} of {page_count}…"
                )

        document.scrub(
            attached_files=True,
            clean_pages=True,
            embedded_files=True,
            hidden_text=False,
            javascript=True,
            metadata=False,
            redactions=True,
            redact_images=0,
            remove_links=True,
            reset_fields=False,
            reset_responses=True,
            thumbnails=True,
            xml_metadata=True,
        )
        if progress_callback and page_count > 0:
            progress_callback(
                f"Securing PDF page {page_count} of {page_count} — rewriting…"
            )

        normalized = document.tobytes(
            garbage=4,
            clean=True,
            deflate=True,
            deflate_images=True,
            deflate_fonts=True,
            use_objstms=1,
        )
        return PdfSanitizationResult(
            pdf_bytes=normalized,
            removed_embedded_files=embedded_files,
            removed_links=links,
        )
    finally:
        document.close()
