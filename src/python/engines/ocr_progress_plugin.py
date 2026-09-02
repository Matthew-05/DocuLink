"""Bridge OCRmyPDF progress bars to DocuLink's NDJSON progress callback."""
from __future__ import annotations

import math
import threading
from collections.abc import Callable

from ocrmypdf import hookimpl


_lock = threading.Lock()
_callback: Callable[[str], None] | None = None
_latest: tuple[str, int, int, str] | None = None

_DESCRIPTION_LABELS = {
    "Scanning contents": "Inspecting PDF",
    "OCR": "OCR",
    "Image processing": "Processing images",
    "hOCR": "Recognizing text",
    "Grafting hOCR to PDF": "Building OCR PDF",
    "PDF/A conversion": "Converting PDF",
    "Linearizing": "Finalizing PDF",
}


def configure_progress_callback(callback: Callable[[str], None] | None) -> None:
    """Set the callback for one synchronous OCRmyPDF invocation."""
    global _callback, _latest
    with _lock:
        _callback = callback
        _latest = None


def progress_message_with_elapsed(elapsed: str) -> str:
    """Return the latest determinate state with elapsed time for heartbeats."""
    with _lock:
        latest = _latest
    if latest is None:
        return f"OCR running — {elapsed} elapsed…"
    label, current, total, unit = latest
    return _format_message(label, current, total, unit, elapsed=elapsed)


def _format_message(
    label: str,
    current: int,
    total: int,
    unit: str,
    *,
    elapsed: str | None = None,
) -> str:
    normalized_unit = (unit or "unit").strip().lower()
    if normalized_unit == "page":
        message = f"{label} page {current} of {total}"
    elif normalized_unit == "image":
        message = f"{label} image {current} of {total}"
    else:
        message = f"{label} {current} of {total}"
    if elapsed:
        return f"{message} — {elapsed} elapsed…"
    return message + "…"


class DocuLinkProgressBar:
    """OCRmyPDF progress-bar protocol implementation with no console output."""

    def __init__(
        self,
        *,
        total: int | float | None = None,
        desc: str | None = None,
        unit: str | None = None,
        unit_scale: float | None = 1.0,
        disable: bool = False,
        **_kwargs,
    ) -> None:
        scale = 1.0 if unit_scale is None else float(unit_scale)
        self.total = max(0, int(round(float(total or 0) * scale)))
        self.label = _DESCRIPTION_LABELS.get(desc or "", desc or "Processing")
        self.unit = unit or "unit"
        self.scale = scale
        self.disable = disable
        self.current = 0.0
        self.last_reported = -1

    def __enter__(self) -> "DocuLinkProgressBar":
        self._report(force=True)
        return self

    def __exit__(self, exc_type, _exc_value, _traceback) -> bool:
        if exc_type is None and self.total > 0:
            self.current = float(self.total)
            self._report()
        return False

    def update(self, n: float = 1, *, completed: float | None = None) -> None:
        if completed is None:
            self.current += float(1 if n is None else n) * self.scale
        else:
            self.current = float(completed) * self.scale
        self._report()

    def _report(self, *, force: bool = False) -> None:
        global _latest
        if self.disable or self.total <= 0:
            return
        completed = min(self.total, max(0, math.floor(self.current + 1e-9)))
        if not force and completed == self.last_reported:
            return
        self.last_reported = completed
        with _lock:
            callback = _callback
            _latest = (self.label, completed, self.total, self.unit)
        if callback is not None:
            callback(_format_message(self.label, completed, self.total, self.unit))


@hookimpl(tryfirst=True)
def get_progressbar_class():
    """Tell OCRmyPDF to report work units through DocuLink."""
    return DocuLinkProgressBar
