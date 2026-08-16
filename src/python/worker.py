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
from engines.table_cell_engine import recover_table_cells
from engines.ocr_engine import (
    MODE_FORCE,
    MODE_REDO,
    PROFILE_DEFAULT,
    active_ocr_engine,
    active_rasterizer,
    evaluate_adaptive_profiles,
    needs_adaptive_retry,
    needs_high_resolution_retry,
    ocr_pdf_bytes,
    profile_ocr_options,
    resolve_use_threads,
    select_best_adaptive_profile,
    summarize_geometry_quality,
)
from ocrmypdf.exceptions import DigitalSignatureError, InputFileError


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
    ocr_mode = "none"
    escalation_reason = ""
    selected_profile = "none"
    evaluated_profiles: list[str] = []
    profile_mean_confidence: float | None = None
    initial_characters = 0
    final_characters = 0
    quality_warning = False
    profile_retry_error = ""
    date_tables_detected = 0
    date_cells_detected = 0
    date_cells_resolved = 0
    date_cells_unresolved = 0
    date_recovery_ms = 0
    date_recovery_error = ""
    table_cells_detected = 0
    table_cells_resolved = 0
    table_cells_unresolved = 0
    table_text_recovery_ms = 0
    table_text_recovery_error = ""
    page_text_regions_detected = 0
    page_text_words_resolved = 0

    def _elapsed_ms(since: float) -> int:
        return int((time.perf_counter() - since) * 1000)

    def _diagnostics(page_count: int) -> dict:
        d = {
            "ocr_engine": active_ocr_engine() if ocr_ran else "none",
            "rasterizer": active_rasterizer() if ocr_ran else "none",
            "page_count": page_count,
            "geometry_ms": geometry_ms,
            "total_ms": _elapsed_ms(started_at),
            "mode": ocr_mode,
            "selected_profile": selected_profile,
            "evaluated_profiles": evaluated_profiles,
            "initial_characters": initial_characters,
            "final_characters": final_characters,
            "quality_warning": quality_warning,
            "date_tables_detected": date_tables_detected,
            "date_cells_detected": date_cells_detected,
            "date_cells_resolved": date_cells_resolved,
            "date_cells_unresolved": date_cells_unresolved,
            "date_recovery_ms": date_recovery_ms,
            "table_cells_detected": table_cells_detected,
            "table_cells_resolved": table_cells_resolved,
            "table_cells_unresolved": table_cells_unresolved,
            "table_text_recovery_ms": table_text_recovery_ms,
            "page_text_regions_detected": page_text_regions_detected,
            "page_text_words_resolved": page_text_words_resolved,
        }
        if ocr_ran:
            d["ocr_ms"] = ocr_ms
            d["use_threads"] = resolve_use_threads()
        if escalation_reason:
            d["escalation_reason"] = escalation_reason
        if profile_mean_confidence is not None:
            d["profile_mean_confidence"] = profile_mean_confidence
        if profile_retry_error:
            d["profile_retry_error"] = profile_retry_error
        if date_recovery_error:
            d["date_recovery_error"] = date_recovery_error
        if table_text_recovery_error:
            d["table_text_recovery_error"] = table_text_recovery_error
        return d

    try:
        pdf_bytes = base64.b64decode(job.pdf_base64)

        if job.mode == "geometry-only":
            geometry_started = time.perf_counter()
            geometry = extract_text_geometry(pdf_bytes, progress_callback=on_progress)
            geometry_ms = _elapsed_ms(geometry_started)

            total_chars = sum(len(p["characters"]) for p in geometry["pages"])
            initial_characters = total_chars
            final_characters = total_chars
            if total_chars == 0 and geometry["pages"]:
                # Pre-detector found text markers but PyMuPDF cannot extract chars
                # (e.g. CID font with no ToUnicode map). Fall back to forced OCR.
                # The text is present but unmappable, so redo would preserve the
                # same unusable glyphs. Rasterizing is the only way to recover
                # characters here, and this job discards the PDF bytes anyway.
                on_progress("Text layer unextractable, falling back to OCR…")
                ocr_started = time.perf_counter()
                result_bytes = ocr_pdf_bytes(
                    pdf_bytes, mode=MODE_FORCE, progress_callback=on_progress
                )
                ocr_ms = _elapsed_ms(ocr_started)
                ocr_ran = True
                ocr_mode = MODE_FORCE
                escalation_reason = "text-unextractable"
                selected_profile = PROFILE_DEFAULT
                evaluated_profiles = [PROFILE_DEFAULT]

                geometry_started = time.perf_counter()
                geometry = extract_text_geometry(result_bytes, progress_callback=on_progress)
                geometry_ms += _elapsed_ms(geometry_started)
                final_characters = sum(
                    len(page["characters"]) for page in geometry["pages"]
                )

            quality_warning = needs_adaptive_retry(summarize_geometry_quality(geometry))

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

        # ── Mode escalation ladder ────────────────────────────────────────────
        # Rung 1 is redo: it replaces the invisible text layer and leaves page
        # content alone, so vector pages stay sharp at any zoom and the stored
        # PDF stays small. Rung 2 is force, which rasterizes every page and
        # permanently discards that fidelity — only taken when redo cannot
        # deliver a usable text layer.
        on_progress("Starting OCR…")
        ocr_started = time.perf_counter()
        ocr_mode = MODE_REDO
        selected_profile = PROFILE_DEFAULT
        evaluated_profiles = [PROFILE_DEFAULT]
        try:
            result_bytes = ocr_pdf_bytes(
                pdf_bytes,
                mode=MODE_REDO,
                progress_callback=on_progress,
            )
        except DigitalSignatureError:
            # Raised before the mode is consulted, so force cannot help either.
            raise
        except InputFileError as exc:
            # redo refuses some inputs outright — notably AcroForm PDFs. Force
            # rewrites the page content and accepts them. Inputs that neither
            # mode can handle (e.g. dynamic XFA) fail again below, and that
            # second failure is the one reported.
            on_progress(f"Redo mode rejected this PDF ({exc}); rasterizing instead…")
            escalation_reason = "input-rejected"
            ocr_mode = MODE_FORCE
            result_bytes = ocr_pdf_bytes(
                pdf_bytes,
                mode=MODE_FORCE,
                progress_callback=on_progress,
            )
        ocr_ms = _elapsed_ms(ocr_started)
        ocr_ran = True

        geometry_started = time.perf_counter()
        geometry = extract_text_geometry(result_bytes, progress_callback=on_progress)
        geometry_ms = _elapsed_ms(geometry_started)
        initial_summary = summarize_geometry_quality(geometry)
        initial_characters = initial_summary["total_characters"]

        # Rung 2, second trigger: redo succeeded but left us with no characters
        # to link against — the CID-font-without-ToUnicode case. Escalate once.
        total_chars = sum(len(p["characters"]) for p in geometry["pages"])
        if total_chars == 0 and geometry["pages"] and ocr_mode == MODE_REDO:
            on_progress("Redo produced no extractable text; rasterizing instead…")
            escalation_reason = "text-unextractable"
            ocr_mode = MODE_FORCE

            ocr_started = time.perf_counter()
            result_bytes = ocr_pdf_bytes(
                pdf_bytes,
                mode=MODE_FORCE,
                progress_callback=on_progress,
            )
            ocr_ms += _elapsed_ms(ocr_started)

            geometry_started = time.perf_counter()
            geometry = extract_text_geometry(result_bytes, progress_callback=on_progress)
            geometry_ms += _elapsed_ms(geometry_started)

        current_summary = summarize_geometry_quality(geometry)

        # ── Adaptive layout retry ─────────────────────────────────────────────
        # A non-empty text layer can still be catastrophically sparse or densely
        # garbled when a scan is embedded below a reliable OCR resolution. In
        # either case, confidence-score high-resolution layout profiles, rerun
        # only the best candidate, and keep it only when extractable coverage
        # materially improves.
        sparse_result = needs_adaptive_retry(current_summary)
        low_resolution_scan = needs_high_resolution_retry(pdf_bytes)
        if (sparse_result or low_resolution_scan) and geometry["pages"]:
            try:
                if sparse_result:
                    on_progress("OCR coverage low; evaluating high-resolution layouts…")
                else:
                    on_progress(
                        "Low-resolution scan detected; evaluating high-resolution layouts…"
                    )
                evaluations = evaluate_adaptive_profiles(pdf_bytes)
                chosen = select_best_adaptive_profile(evaluations)
                evaluated_profiles = [PROFILE_DEFAULT] + [
                    evaluation["profile"] for evaluation in evaluations
                ]

                candidate_profile = chosen["profile"]
                candidate_options = profile_ocr_options(candidate_profile)
                on_progress(
                    f"Retrying OCR with {candidate_profile} at "
                    f"{candidate_options['oversample']} DPI…"
                )

                retry_started = time.perf_counter()
                retry_bytes = ocr_pdf_bytes(
                    pdf_bytes,
                    mode=ocr_mode,
                    tesseract_pagesegmode=candidate_options[
                        "tesseract_pagesegmode"
                    ],
                    oversample=candidate_options["oversample"],
                    progress_callback=on_progress,
                )
                ocr_ms += _elapsed_ms(retry_started)

                retry_geometry_started = time.perf_counter()
                retry_geometry = extract_text_geometry(
                    retry_bytes, progress_callback=on_progress
                )
                geometry_ms += _elapsed_ms(retry_geometry_started)
                retry_summary = summarize_geometry_quality(retry_geometry)

                def coverage_score(summary: dict) -> int:
                    return (
                        int(summary["alphanumeric_characters"])
                        + int(summary["word_count"]) * 4
                        + int(summary["populated_lines"]) * 8
                    )

                if coverage_score(retry_summary) > coverage_score(current_summary):
                    result_bytes = retry_bytes
                    geometry = retry_geometry
                    current_summary = retry_summary
                    selected_profile = candidate_profile
                    profile_mean_confidence = chosen["mean_confidence"]
                    if low_resolution_scan and not escalation_reason:
                        escalation_reason = "low-effective-dpi"
                    on_progress(f"Selected improved OCR profile: {candidate_profile}")
                else:
                    on_progress("Adaptive OCR did not improve coverage; keeping first pass…")
            except Exception as exc:  # noqa: BLE001 — optional retry must be safe
                profile_retry_error = str(exc)
                on_progress("Adaptive OCR retry unavailable; keeping first pass…")

        # ── Ruled-table cell recovery ────────────────────────────────────────
        # Whole-page OCR can recognize a dense table while still dropping rows
        # or confusing tiny glyphs. Detect the grid and headers, remove only the
        # rulings from recognition crops, apply column-aware cell profiles, and
        # rebuild the table in deterministic row-major reading order.
        date_recovery_started = time.perf_counter()
        try:
            recovered_bytes, date_stats = recover_table_cells(
                pdf_bytes,
                result_bytes,
                progress_callback=on_progress,
            )
            date_tables_detected = int(date_stats["date_tables_detected"])
            date_cells_detected = int(date_stats["date_cells_detected"])
            date_cells_resolved = int(date_stats["date_cells_resolved"])
            date_cells_unresolved = int(date_stats["date_cells_unresolved"])
            table_cells_detected = int(date_stats["table_cells_detected"])
            table_cells_resolved = int(date_stats["table_cells_resolved"])
            table_cells_unresolved = int(date_stats["table_cells_unresolved"])
            page_text_regions_detected = int(date_stats["page_text_regions_detected"])
            page_text_words_resolved = int(date_stats["page_text_words_resolved"])
            date_recovery_ms = _elapsed_ms(date_recovery_started)
            table_text_recovery_ms = date_recovery_ms

            if date_stats["changed"]:
                result_bytes = recovered_bytes
                corrected_geometry_started = time.perf_counter()
                geometry = extract_text_geometry(
                    result_bytes,
                    progress_callback=on_progress,
                )
                geometry_ms += _elapsed_ms(corrected_geometry_started)
                current_summary = summarize_geometry_quality(geometry)
                on_progress(
                    f"Recovered {table_cells_resolved} of "
                    f"{table_cells_detected} ruled-table cells"
                )
        except Exception as exc:  # noqa: BLE001 — optional recovery must be safe
            date_recovery_error = str(exc)
            table_text_recovery_error = str(exc)
            on_progress("Table-cell recovery unavailable; keeping selected OCR result…")
        finally:
            if date_recovery_ms == 0:
                date_recovery_ms = _elapsed_ms(date_recovery_started)
            if table_text_recovery_ms == 0:
                table_text_recovery_ms = date_recovery_ms

        final_characters = current_summary["total_characters"]
        quality_warning = needs_adaptive_retry(current_summary)

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
