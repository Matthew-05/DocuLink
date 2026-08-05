"""
DocuLink worker — stdin/stdout JSON-line protocol.

The C# host spawns this process once and sends newline-delimited JSON jobs
via stdin. For each job this worker writes one or more progress lines followed
by exactly one success/error result line, all to stdout.

Two commands are supported:
  • "ocr"     — add a text layer to a PDF and/or extract its text geometry
  • "convert" — turn a non-PDF source document into a PDF (or into HTML when
                only the host can render it)

Protocol is defined in contracts/python-worker-v1.json.
"""
from __future__ import annotations

import base64
import json
import os
import sys
import time

from engines.conversion_engine import ConversionError, convert_to_pdf
from engines.geometry_engine import extract_text_geometry, geometry_to_base64
from engines.ocr_engine import (
    active_ocr_engine,
    active_rasterizer,
    ocr_pdf_bytes,
    resolve_use_threads,
)


def _claim_protocol_stream():
    """
    Take exclusive ownership of the real stdout for protocol traffic.

    stdout carries the NDJSON protocol, so a single stray write from any library
    or child process corrupts it. We duplicate the real stdout onto a private
    descriptor and repoint fd 1 at the null device, so accidental writes are
    discarded instead of injected into the stream. This matters most when
    OCRmyPDF runs with use_threads=False: its worker processes inherit fd 1.

    The host sets RedirectStandardError=false, so there is no stderr pipe that
    could fill and deadlock us. Falls back to plain sys.stdout if the platform
    refuses the descriptor juggling.
    """
    try:
        protocol_fd = os.dup(1)
        devnull_fd = os.open(os.devnull, os.O_WRONLY)
        os.dup2(devnull_fd, 1)
        os.close(devnull_fd)
        stream = os.fdopen(protocol_fd, "w", encoding="utf-8", newline="\n")
        sys.stdout = open(os.devnull, "w", encoding="utf-8")
        return stream
    except OSError:
        return sys.stdout


_PROTOCOL_OUT = _claim_protocol_stream()
from schemas.models import ConvertJob, ConvertResult, OcrJob, OcrProgress, OcrResult


def _write(obj: dict) -> None:
    _PROTOCOL_OUT.write(json.dumps(obj, separators=(",", ":")) + "\n")
    _PROTOCOL_OUT.flush()


def _handle_convert_job(job: ConvertJob) -> None:
    def on_progress(message: str) -> None:
        _write(OcrProgress(job_id=job.job_id, message=message).to_dict())

    try:
        source_bytes = base64.b64decode(job.source_base64)
        output = convert_to_pdf(
            source_bytes,
            job.source_extension,
            source_name=job.source_name,
            progress_callback=on_progress,
        )

        if output.kind == "pdf":
            _write(
                ConvertResult(
                    job_id=job.job_id,
                    status="success",
                    output_kind="pdf",
                    pdf_base64=base64.b64encode(output.pdf_bytes).decode("ascii"),
                ).to_dict()
            )
        else:
            _write(
                ConvertResult(
                    job_id=job.job_id,
                    status="success",
                    output_kind="html",
                    html=output.html,
                ).to_dict()
            )
    except ConversionError as exc:
        _write(ConvertResult(job_id=job.job_id, status="error", error=str(exc)).to_dict())
    except Exception as exc:  # noqa: BLE001
        _write(
            ConvertResult(
                job_id=job.job_id,
                status="error",
                error=f"Conversion failed: {exc}",
            ).to_dict()
        )


def _handle_job(job: OcrJob) -> None:
    def on_progress(message: str) -> None:
        _write(OcrProgress(job_id=job.job_id, message=message).to_dict())

    # OcrDiagnostics accumulator (contracts/python-worker-v1.json). Populated as
    # the job progresses so the host debug log records what actually ran, not
    # what we intended to run. ocr_ms stays absent unless OCRmyPDF is invoked.
    started_at = time.perf_counter()
    ocr_ms = 0
    ocr_ran = False
    geometry_ms = 0

    def _elapsed_ms(since: float) -> int:
        return int((time.perf_counter() - since) * 1000)

    def _diagnostics(page_count: int) -> dict:
        d = {
            "ocr_engine": active_ocr_engine() if ocr_ran else "none",
            "rasterizer": active_rasterizer() if ocr_ran else "none",
            "page_count": page_count,
            "geometry_ms": geometry_ms,
            "total_ms": _elapsed_ms(started_at),
        }
        if ocr_ran:
            d["ocr_ms"] = ocr_ms
            d["use_threads"] = resolve_use_threads()
        return d

    try:
        pdf_bytes = base64.b64decode(job.pdf_base64)

        if job.mode == "geometry-only":
            geometry_started = time.perf_counter()
            geometry = extract_text_geometry(pdf_bytes, progress_callback=on_progress)
            geometry_ms = _elapsed_ms(geometry_started)

            total_chars = sum(len(p["characters"]) for p in geometry["pages"])
            if total_chars == 0 and geometry["pages"]:
                # Pre-detector found text markers but PyMuPDF cannot extract chars
                # (e.g. CID font with no ToUnicode map). Fall back to forced OCR.
                on_progress("Text layer unextractable, falling back to OCR…")
                ocr_started = time.perf_counter()
                result_bytes = ocr_pdf_bytes(pdf_bytes, force_ocr=True, progress_callback=on_progress)
                ocr_ms = _elapsed_ms(ocr_started)
                ocr_ran = True

                geometry_started = time.perf_counter()
                geometry = extract_text_geometry(result_bytes, progress_callback=on_progress)
                geometry_ms += _elapsed_ms(geometry_started)

            geometry_b64 = geometry_to_base64(geometry)
            _write(
                OcrResult(
                    job_id=job.job_id,
                    status="success",
                    geometry_base64=geometry_b64,
                    diagnostics=_diagnostics(len(geometry["pages"])),
                ).to_dict()
            )
            return

        on_progress("Starting OCR…")
        ocr_started = time.perf_counter()
        result_bytes = ocr_pdf_bytes(
            pdf_bytes,
            force_ocr=True,
            progress_callback=on_progress,
        )
        ocr_ms = _elapsed_ms(ocr_started)
        ocr_ran = True

        geometry_started = time.perf_counter()
        geometry = extract_text_geometry(result_bytes, progress_callback=on_progress)
        geometry_ms = _elapsed_ms(geometry_started)

        geometry_b64 = geometry_to_base64(geometry)
        result_b64 = base64.b64encode(result_bytes).decode("ascii")
        _write(
            OcrResult(
                job_id=job.job_id,
                status="success",
                pdf_base64=result_b64,
                geometry_base64=geometry_b64,
                diagnostics=_diagnostics(len(geometry["pages"])),
            ).to_dict()
        )
    except Exception as exc:  # noqa: BLE001
        _write(OcrResult(job_id=job.job_id, status="error", error=str(exc)).to_dict())


def main() -> None:
    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue
        try:
            data = json.loads(line)
            command = data.get("command", "ocr")
            if command == "convert":
                convert_job = ConvertJob.from_dict(data)
            else:
                ocr_job = OcrJob.from_dict(data)
        except (json.JSONDecodeError, KeyError, AttributeError) as exc:
            # Emit an error without a real job_id so the host can log it.
            _write({"job_id": "", "status": "error", "error": f"Invalid job: {exc}"})
            continue

        if command == "convert":
            _handle_convert_job(convert_job)
        else:
            _handle_job(ocr_job)


if __name__ == "__main__":
    # Required before anything else when OCRmyPDF runs with use_threads=False and
    # this worker is a frozen PyInstaller bundle (see worker.spec): without it,
    # each spawned child re-executes the worker entry point instead of the pool
    # task. Harmless in the embeddable-Python build, where the __main__ guard
    # above already covers spawn re-import.
    import multiprocessing

    multiprocessing.freeze_support()
    main()
