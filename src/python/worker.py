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
from collections import OrderedDict
import hashlib
import json
import os
import sys
import time

from engines.conversion_engine import ConversionError, convert_to_pdf
from engines.geometry_engine import extract_text_geometry, geometry_to_base64
from engines.pdf_security import sanitize_pdf_bytes
from engines.table_cell_engine import has_recoverable_ruled_table, recover_table_cells
from engines.table.detector import detect_tables, structure_to_base64
from engines.ocr_engine import (
    MODE_FORCE,
    MODE_REDO,
    PROFILE_DEFAULT,
    active_ocr_engine,
    active_rasterizer,
    detect_direct_page_rotations,
    evaluate_adaptive_profiles,
    extract_direct_text_geometry,
    merge_geometry_pages,
    merge_missing_text_lines,
    needs_adaptive_retry,
    needs_garbled_text_retry,
    needs_high_resolution_retry,
    needs_low_resolution_quality_retry,
    ocr_pdf_bytes,
    profile_ocr_options,
    resolve_use_threads,
    select_pages_requiring_ocr,
    select_best_adaptive_profile,
    should_merge_faint_ink_retry,
    should_select_rotated_retry,
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


_GEOMETRY_CACHE_VERSION = "direct-hocr-v8-table-period"
_GEOMETRY_CACHE_MAX_ENTRIES = 16
_GEOMETRY_CACHE: OrderedDict[str, dict] = OrderedDict()


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
    input_bytes = 0
    output_bytes = 0
    page_count = 0
    decode_ms = 0
    input_sanitize_ms = 0
    output_sanitize_ms = 0
    inspect_ms = 0
    preflight_geometry_ms = 0
    ocr_ms = 0
    ocr_ran = False
    geometry_ms = 0
    primary_ocr_ms = 0
    fallback_ocr_ms = 0
    adaptive_detection_ms = 0
    adaptive_evaluation_ms = 0
    adaptive_ocr_ms = 0
    primary_geometry_ms = 0
    fallback_geometry_ms = 0
    adaptive_geometry_ms = 0
    table_geometry_ms = 0
    geometry_encode_ms = 0
    pdf_encode_ms = 0
    ocr_mode = "none"
    escalation_reason = ""
    selected_profile = "none"
    evaluated_profiles: list[str] = []
    profile_mean_confidence: float | None = None
    initial_characters = 0
    initial_non_whitespace_characters = 0
    initial_alphanumeric_ratio = 0.0
    initial_garbled_page_numbers: list[int] = []
    final_characters = 0
    final_non_whitespace_characters = 0
    final_alphanumeric_ratio = 0.0
    final_garbled_page_numbers: list[int] = []
    forced_page_numbers: list[int] = []
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
    table_images_examined = 0
    table_images_skipped_small = 0
    table_grid_candidates = 0
    table_text_recovery_ms = 0
    table_text_recovery_error = ""
    table_structure_ms = 0
    table_structure_error = ""
    # Detector diagnostics travel as their own dictionary so the detector can add
    # counters without every caller learning their names.
    table_diagnostics: dict = {}
    page_text_regions_detected = 0
    page_text_words_resolved = 0
    native_pages_reused = 0
    direct_ocr_page_numbers: list[int] = []
    direct_retry_page_numbers: list[int] = []
    direct_400_selected_page_numbers: list[int] = []
    direct_rotated_page_numbers: list[int] = []
    direct_mean_confidence: float | None = None
    direct_ocr_error = ""
    table_detection_ms = 0
    legacy_table_required = False
    geometry_cache_key = ""
    geometry_cache_hit = False
    pdf_sanitized = False
    removed_embedded_files = 0
    removed_links = 0

    def _elapsed_ms(since: float) -> int:
        return int((time.perf_counter() - since) * 1000)

    def _diagnostics(page_count: int) -> dict:
        d = {
            "ocr_engine": active_ocr_engine() if ocr_ran else "none",
            "rasterizer": (
                "pymupdf"
                if ocr_mode == "direct"
                else active_rasterizer() if ocr_ran else "none"
            ),
            "page_count": page_count,
            "input_bytes": input_bytes,
            "output_bytes": output_bytes,
            "decode_ms": decode_ms,
            "input_sanitize_ms": input_sanitize_ms,
            "output_sanitize_ms": output_sanitize_ms,
            "inspect_ms": inspect_ms,
            "preflight_geometry_ms": preflight_geometry_ms,
            "primary_ocr_ms": primary_ocr_ms,
            "fallback_ocr_ms": fallback_ocr_ms,
            "adaptive_detection_ms": adaptive_detection_ms,
            "adaptive_evaluation_ms": adaptive_evaluation_ms,
            "adaptive_ocr_ms": adaptive_ocr_ms,
            "primary_geometry_ms": primary_geometry_ms,
            "fallback_geometry_ms": fallback_geometry_ms,
            "adaptive_geometry_ms": adaptive_geometry_ms,
            "table_geometry_ms": table_geometry_ms,
            "geometry_encode_ms": geometry_encode_ms,
            "pdf_encode_ms": pdf_encode_ms,
            "source_pdf_preserved": job.preserve_source_pdf,
            "geometry_ms": geometry_ms,
            "total_ms": _elapsed_ms(started_at),
            "mode": ocr_mode,
            "selected_profile": selected_profile,
            "evaluated_profiles": evaluated_profiles,
            "initial_characters": initial_characters,
            "initial_non_whitespace_characters": initial_non_whitespace_characters,
            "initial_alphanumeric_ratio": initial_alphanumeric_ratio,
            "initial_garbled_page_count": len(initial_garbled_page_numbers),
            "initial_garbled_page_numbers": initial_garbled_page_numbers,
            "final_characters": final_characters,
            "final_non_whitespace_characters": final_non_whitespace_characters,
            "final_alphanumeric_ratio": final_alphanumeric_ratio,
            "final_garbled_page_count": len(final_garbled_page_numbers),
            "final_garbled_page_numbers": final_garbled_page_numbers,
            "forced_page_count": len(forced_page_numbers),
            "forced_page_numbers": forced_page_numbers,
            "quality_warning": quality_warning,
            "date_tables_detected": date_tables_detected,
            "date_cells_detected": date_cells_detected,
            "date_cells_resolved": date_cells_resolved,
            "date_cells_unresolved": date_cells_unresolved,
            "date_recovery_ms": date_recovery_ms,
            "table_cells_detected": table_cells_detected,
            "table_cells_resolved": table_cells_resolved,
            "table_cells_unresolved": table_cells_unresolved,
            "table_images_examined": table_images_examined,
            "table_images_skipped_small": table_images_skipped_small,
            "table_grid_candidates": table_grid_candidates,
            "table_text_recovery_ms": table_text_recovery_ms,
            "table_structure_ms": table_structure_ms,
            "page_text_regions_detected": page_text_regions_detected,
            "page_text_words_resolved": page_text_words_resolved,
            "native_pages_reused": native_pages_reused,
            "direct_ocr_page_count": len(direct_ocr_page_numbers),
            "direct_ocr_page_numbers": direct_ocr_page_numbers,
            "direct_retry_page_count": len(direct_retry_page_numbers),
            "direct_retry_page_numbers": direct_retry_page_numbers,
            "direct_400_selected_page_numbers": direct_400_selected_page_numbers,
            "direct_rotated_page_numbers": direct_rotated_page_numbers,
            "table_detection_ms": table_detection_ms,
            "geometry_cache_hit": geometry_cache_hit,
            "geometry_cache_version": _GEOMETRY_CACHE_VERSION,
            "pdf_sanitized": pdf_sanitized,
            "removed_embedded_files": removed_embedded_files,
            "removed_links": removed_links,
        }
        if ocr_ran:
            d["ocr_ms"] = ocr_ms
            d["ocr_optimize"] = 0
            if ocr_mode != "direct":
                d["use_threads"] = resolve_use_threads()
        if escalation_reason:
            d["escalation_reason"] = escalation_reason
        if profile_mean_confidence is not None:
            d["profile_mean_confidence"] = profile_mean_confidence
        if direct_mean_confidence is not None:
            d["direct_mean_confidence"] = direct_mean_confidence
        if direct_ocr_error:
            d["direct_ocr_error"] = direct_ocr_error
        if profile_retry_error:
            d["profile_retry_error"] = profile_retry_error
        if date_recovery_error:
            d["date_recovery_error"] = date_recovery_error
        if table_text_recovery_error:
            d["table_text_recovery_error"] = table_text_recovery_error
        if table_structure_error:
            d["table_structure_error"] = table_structure_error
        d.update(table_diagnostics)
        return d

    def _emit_success(
        geometry: dict,
        result_bytes: bytes | None = None,
        *,
        result_is_sanitized: bool = False,
    ) -> None:
        nonlocal final_characters
        nonlocal final_non_whitespace_characters
        nonlocal final_alphanumeric_ratio
        nonlocal final_garbled_page_numbers
        nonlocal quality_warning
        nonlocal output_bytes
        nonlocal geometry_encode_ms
        nonlocal pdf_encode_ms
        nonlocal pdf_sanitized
        nonlocal output_sanitize_ms
        nonlocal removed_embedded_files
        nonlocal removed_links
        nonlocal table_structure_ms
        nonlocal table_structure_error

        final_summary = summarize_geometry_quality(geometry)
        final_characters = final_summary["total_characters"]
        final_non_whitespace_characters = final_summary[
            "non_whitespace_characters"
        ]
        final_alphanumeric_ratio = final_summary["alphanumeric_ratio"]
        final_garbled_page_numbers = list(
            final_summary["garbled_page_numbers"]
        )
        quality_warning = needs_adaptive_retry(
            final_summary
        ) or needs_garbled_text_retry(final_summary)

        if (
            result_bytes is not None
            and not job.preserve_source_pdf
            and not result_is_sanitized
        ):
            output_sanitize_started = time.perf_counter()
            sanitized = sanitize_pdf_bytes(result_bytes)
            output_sanitize_ms = _elapsed_ms(output_sanitize_started)
            result_bytes = sanitized.pdf_bytes
            removed_embedded_files += sanitized.removed_embedded_files
            removed_links += sanitized.removed_links
            pdf_sanitized = True

        output_bytes = len(result_bytes) if result_bytes is not None else 0
        geometry_encode_started = time.perf_counter()
        geometry_b64 = geometry_to_base64(geometry)
        geometry_encode_ms = _elapsed_ms(geometry_encode_started)
        table_structure_b64 = ""
        table_structure_started = time.perf_counter()
        try:
            on_progress("Detecting table structure…")
            detection_pdf = pdf_bytes if job.preserve_source_pdf or result_bytes is None else result_bytes
            table_structure = detect_tables(
                detection_pdf,
                geometry,
                progress_callback=on_progress,
                diagnostics=table_diagnostics,
            )
            table_structure_b64 = structure_to_base64(table_structure)
        except Exception as exc:  # noqa: BLE001 — optional stage must preserve OCR
            table_structure_error = str(exc)
            on_progress("Table structure detection unavailable; keeping OCR result…")
        finally:
            table_structure_ms = _elapsed_ms(table_structure_started)
        result_b64 = ""
        if result_bytes is not None and not job.preserve_source_pdf:
            pdf_encode_started = time.perf_counter()
            result_b64 = base64.b64encode(result_bytes).decode("ascii")
            pdf_encode_ms = _elapsed_ms(pdf_encode_started)
        _write(
            OcrResult(
                job_id=job.job_id,
                status="success",
                pdf_base64=result_b64,
                geometry_base64=geometry_b64,
                table_structure_base64=table_structure_b64,
                diagnostics=_diagnostics(len(geometry["pages"])),
            ).to_dict()
        )
        if geometry_cache_key and job.preserve_source_pdf and job.mode == "full":
            _GEOMETRY_CACHE[geometry_cache_key] = {
                "geometry_base64": geometry_b64,
                "table_structure_base64": table_structure_b64,
                "summary": final_summary,
                "quality_warning": quality_warning,
            }
            _GEOMETRY_CACHE.move_to_end(geometry_cache_key)
            while len(_GEOMETRY_CACHE) > _GEOMETRY_CACHE_MAX_ENTRIES:
                _GEOMETRY_CACHE.popitem(last=False)

    try:
        decode_started = time.perf_counter()
        pdf_bytes = base64.b64decode(job.pdf_base64)
        decode_ms = _elapsed_ms(decode_started)
        input_bytes = len(pdf_bytes)

        # OCR works on a passive copy. This strips attachments, JavaScript and
        # interactive actions before third-party PDF engines inspect the file.
        input_sanitize_started = time.perf_counter()
        sanitized_source = sanitize_pdf_bytes(pdf_bytes, progress_callback=on_progress)
        input_sanitize_ms = _elapsed_ms(input_sanitize_started)
        pdf_bytes = sanitized_source.pdf_bytes
        removed_embedded_files = sanitized_source.removed_embedded_files
        removed_links = sanitized_source.removed_links
        pdf_sanitized = True

        inspect_started = time.perf_counter()
        import pymupdf as fitz

        inspected_doc = fitz.open(stream=pdf_bytes, filetype="pdf")
        try:
            page_count = inspected_doc.page_count
        finally:
            inspected_doc.close()
        inspect_ms = _elapsed_ms(inspect_started)

        if job.mode == "full" and job.preserve_source_pdf:
            geometry_cache_key = hashlib.sha256(
                (_GEOMETRY_CACHE_VERSION + ":").encode("ascii") + pdf_bytes
            ).hexdigest()
            cached = _GEOMETRY_CACHE.get(geometry_cache_key)
            if cached is not None:
                _GEOMETRY_CACHE.move_to_end(geometry_cache_key)
                geometry_cache_hit = True
                cached_summary = cached["summary"]
                initial_characters = int(cached_summary["total_characters"])
                initial_non_whitespace_characters = int(
                    cached_summary["non_whitespace_characters"]
                )
                initial_alphanumeric_ratio = float(
                    cached_summary["alphanumeric_ratio"]
                )
                initial_garbled_page_numbers = list(
                    cached_summary["garbled_page_numbers"]
                )
                final_characters = initial_characters
                final_non_whitespace_characters = (
                    initial_non_whitespace_characters
                )
                final_alphanumeric_ratio = initial_alphanumeric_ratio
                final_garbled_page_numbers = initial_garbled_page_numbers
                quality_warning = bool(cached["quality_warning"])
                ocr_mode = "none"
                selected_profile = "none"
                evaluated_profiles = []
                on_progress("Identical PDF already processed; reusing geometry…")
                _write(
                    OcrResult(
                        job_id=job.job_id,
                        status="success",
                        geometry_base64=cached["geometry_base64"],
                        table_structure_base64=cached.get("table_structure_base64", ""),
                        diagnostics=_diagnostics(page_count),
                    ).to_dict()
                )
                return

        if job.mode == "geometry-only":
            geometry_started = time.perf_counter()
            geometry = extract_text_geometry(pdf_bytes, progress_callback=on_progress)
            primary_geometry_ms = _elapsed_ms(geometry_started)
            geometry_ms = primary_geometry_ms

            initial_summary = summarize_geometry_quality(geometry)
            initial_characters = initial_summary["total_characters"]
            initial_non_whitespace_characters = initial_summary[
                "non_whitespace_characters"
            ]
            initial_alphanumeric_ratio = initial_summary["alphanumeric_ratio"]
            initial_garbled_page_numbers = list(
                initial_summary["garbled_page_numbers"]
            )
            garbled_text = needs_garbled_text_retry(initial_summary)
            if (initial_characters == 0 or garbled_text) and geometry["pages"]:
                # Pre-detector found text markers but PyMuPDF cannot extract chars
                # or extracted dense punctuation garbage from a broken character
                # map. Redo would preserve those unusable glyphs, so rasterize.
                if garbled_text:
                    forced_page_numbers = initial_garbled_page_numbers
                    on_progress(
                        "Text layer appears garbled on page(s) "
                        f"{','.join(map(str, forced_page_numbers))}; "
                        "rasterizing only those pages…"
                    )
                    escalation_reason = "text-garbled"
                else:
                    forced_page_numbers = list(range(1, page_count + 1))
                    on_progress("Text layer unextractable, falling back to OCR…")
                    escalation_reason = "text-unextractable"
                ocr_started = time.perf_counter()
                ocr_ran = True
                ocr_mode = MODE_FORCE
                selected_profile = PROFILE_DEFAULT
                evaluated_profiles = [PROFILE_DEFAULT]
                try:
                    result_bytes = ocr_pdf_bytes(
                        pdf_bytes,
                        mode=MODE_FORCE,
                        auto_rotate_pages=not job.preserve_source_pdf,
                        pages=",".join(map(str, forced_page_numbers)) or None,
                        progress_callback=on_progress,
                    )
                finally:
                    fallback_ocr_ms = _elapsed_ms(ocr_started)
                    ocr_ms = fallback_ocr_ms

                geometry_started = time.perf_counter()
                geometry = extract_text_geometry(result_bytes, progress_callback=on_progress)
                fallback_geometry_ms = _elapsed_ms(geometry_started)
                geometry_ms += fallback_geometry_ms
            final_summary = summarize_geometry_quality(geometry)
            final_characters = final_summary["total_characters"]
            final_non_whitespace_characters = final_summary[
                "non_whitespace_characters"
            ]
            final_alphanumeric_ratio = final_summary["alphanumeric_ratio"]
            final_garbled_page_numbers = list(
                final_summary["garbled_page_numbers"]
            )
            quality_warning = needs_adaptive_retry(
                final_summary
            ) or needs_garbled_text_retry(final_summary)

            geometry_encode_started = time.perf_counter()
            geometry_b64 = geometry_to_base64(geometry)
            geometry_encode_ms = _elapsed_ms(geometry_encode_started)
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
        # Validate the source geometry first. This is cheap compared with OCR and
        # lets a provably broken character map bypass redo entirely. Clean pages
        # are left untouched while only the corrupt page numbers are force-OCR'd.
        preflight_started = time.perf_counter()
        preflight_geometry = extract_text_geometry(
            pdf_bytes,
            progress_callback=on_progress,
            progress_label="Checking source text",
        )
        preflight_geometry_ms = _elapsed_ms(preflight_started)
        preflight_summary = summarize_geometry_quality(preflight_geometry)
        preflight_garbled = needs_garbled_text_retry(preflight_summary)

        initial_summary = preflight_summary
        initial_characters = initial_summary["total_characters"]
        initial_non_whitespace_characters = initial_summary[
            "non_whitespace_characters"
        ]
        initial_alphanumeric_ratio = initial_summary["alphanumeric_ratio"]
        initial_garbled_page_numbers = list(
            initial_summary["garbled_page_numbers"]
        )

        # The viewer retains the source PDF, so most jobs no longer need an OCR
        # PDF at all. Reuse trustworthy native geometry and send only suspicious
        # pages through Tesseract hOCR, which already includes character boxes.
        if job.preserve_source_pdf:
            direct_ocr_page_numbers = select_pages_requiring_ocr(
                pdf_bytes,
                preflight_summary,
            )
            native_pages_reused = page_count - len(direct_ocr_page_numbers)
            if not direct_ocr_page_numbers:
                ocr_mode = "none"
                selected_profile = "none"
                evaluated_profiles = []
                ocr_ran = False
                on_progress("Source text geometry is trustworthy; skipping OCR…")
                _emit_success(preflight_geometry)
                return

            table_detection_started = time.perf_counter()
            try:
                legacy_table_required, table_scan_stats = (
                    has_recoverable_ruled_table(pdf_bytes)
                )
                table_images_examined = int(
                    table_scan_stats["table_images_examined"]
                )
                table_images_skipped_small = int(
                    table_scan_stats["table_images_skipped_small"]
                )
                table_grid_candidates = int(
                    table_scan_stats["table_grid_candidates"]
                )
            except Exception as exc:  # noqa: BLE001 — compatibility fallback
                legacy_table_required = True
                direct_ocr_error = "Table compatibility check failed: " + str(exc)
            finally:
                table_detection_ms = _elapsed_ms(table_detection_started)

            if not legacy_table_required:
                try:
                    ocr_mode = "direct"
                    selected_profile = "direct-hocr"
                    evaluated_profiles = ["direct-hocr-300"]
                    forced_page_numbers = direct_ocr_page_numbers
                    escalation_reason = (
                        "text-garbled-preflight"
                        if preflight_garbled
                        else "page-needs-ocr"
                    )
                    ocr_ran = True
                    on_progress(
                        f"Direct OCR on {len(direct_ocr_page_numbers)} page(s) "
                        "at 300 DPI…"
                    )
                    direct_started = time.perf_counter()
                    direct_pages, direct_stats = extract_direct_text_geometry(
                        pdf_bytes,
                        direct_ocr_page_numbers,
                        dpi=300,
                        progress_callback=on_progress,
                    )
                    primary_ocr_ms = _elapsed_ms(direct_started)

                    orientation_check_page_numbers = [
                        page_number
                        for page_number, stats in direct_stats.items()
                        if float(stats["mean_confidence"]) < 75.0
                        or int(stats["character_count"]) < 40
                    ]
                    detected_rotations = detect_direct_page_rotations(
                        pdf_bytes,
                        orientation_check_page_numbers,
                        progress_callback=on_progress,
                    )
                    if detected_rotations:
                        evaluated_profiles.append("direct-hocr-oriented-300")
                        on_progress(
                            f"Retrying {len(detected_rotations)} confidently "
                            "sideways page(s) upright…"
                        )
                        rotation_retry_started = time.perf_counter()
                        rotated_pages, rotated_stats = (
                            extract_direct_text_geometry(
                                pdf_bytes,
                                list(detected_rotations),
                                dpi=300,
                                page_rotations=detected_rotations,
                                progress_callback=on_progress,
                            )
                        )
                        adaptive_ocr_ms += _elapsed_ms(rotation_retry_started)
                        for page_number in sorted(detected_rotations):
                            first = direct_stats[page_number]
                            retry = rotated_stats[page_number]
                            if not should_select_rotated_retry(first, retry):
                                continue
                            direct_pages[page_number] = rotated_pages[page_number]
                            direct_stats[page_number] = retry
                            direct_rotated_page_numbers.append(page_number)

                    direct_retry_page_numbers = [
                        page_number
                        for page_number, stats in direct_stats.items()
                        if float(stats["mean_confidence"]) < 75.0
                        or int(stats["character_count"]) < 40
                    ]
                    faint_ink_page_numbers = [
                        page_number
                        for page_number in direct_retry_page_numbers
                        if int(direct_stats[page_number]["character_count"]) >= 40
                        and float(direct_stats[page_number]["mean_confidence"]) < 75.0
                    ]
                    if faint_ink_page_numbers:
                        evaluated_profiles.append(
                            "direct-hocr-faint-ink-psm6-300"
                        )
                        on_progress(
                            f"Enhancing {len(faint_ink_page_numbers)} faint or "
                            "uneven scan page(s)…"
                        )
                        retry_started = time.perf_counter()
                        enhanced_pages, enhanced_stats = (
                            extract_direct_text_geometry(
                                pdf_bytes,
                                faint_ink_page_numbers,
                                dpi=300,
                                psm=6,
                                preprocessing="faint-ink",
                                progress_callback=on_progress,
                            )
                        )
                        adaptive_ocr_ms += _elapsed_ms(retry_started)
                        for page_number in faint_ink_page_numbers:
                            first = direct_stats[page_number]
                            enhanced = enhanced_stats[page_number]
                            if not should_merge_faint_ink_retry(first, enhanced):
                                continue
                            (
                                merged_page,
                                changed_lines,
                                word_delta,
                                character_delta,
                            ) = merge_missing_text_lines(
                                direct_pages[page_number],
                                enhanced_pages[page_number],
                                first.get("line_stats"),
                                enhanced.get("line_stats"),
                            )
                            if changed_lines <= 0:
                                continue
                            direct_pages[page_number] = merged_page
                            first["word_count"] = (
                                int(first["word_count"]) + word_delta
                            )
                            first["character_count"] = (
                                int(first["character_count"]) + character_delta
                            )

                    high_resolution_page_numbers = [
                        page_number
                        for page_number in direct_retry_page_numbers
                        if int(direct_stats[page_number]["character_count"]) < 40
                    ]
                    if high_resolution_page_numbers:
                        evaluated_profiles.append("direct-hocr-400")
                        on_progress(
                            f"Retrying {len(high_resolution_page_numbers)} sparse "
                            "page(s) at 400 DPI…"
                        )
                        retry_started = time.perf_counter()
                        retry_pages, retry_stats = extract_direct_text_geometry(
                            pdf_bytes,
                            high_resolution_page_numbers,
                            dpi=400,
                            progress_callback=on_progress,
                        )
                        adaptive_ocr_ms += _elapsed_ms(retry_started)
                        for page_number in high_resolution_page_numbers:
                            first = direct_stats[page_number]
                            retry = retry_stats[page_number]
                            first_characters = int(first["character_count"])
                            retry_characters = int(retry["character_count"])
                            first_confidence = float(first["mean_confidence"])
                            retry_confidence = float(retry["mean_confidence"])
                            if (
                                retry_characters > first_characters * 1.05
                                or retry_confidence > first_confidence + 2.0
                            ):
                                direct_pages[page_number] = retry_pages[page_number]
                                direct_stats[page_number] = retry
                                direct_400_selected_page_numbers.append(page_number)

                    sparse_after_dpi_retry = [
                        page_number
                        for page_number in direct_ocr_page_numbers
                        if int(direct_stats[page_number]["word_count"]) == 0
                        or int(direct_stats[page_number]["character_count"]) < 40
                    ]
                    if sparse_after_dpi_retry:
                        evaluated_profiles.append("direct-hocr-cropped-psm4-300")
                        on_progress(
                            f"Retrying {len(sparse_after_dpi_retry)} empty or sparse "
                            "page(s) with cropped single-column OCR…"
                        )
                        crop_retry_started = time.perf_counter()
                        cropped_pages, cropped_stats = extract_direct_text_geometry(
                            pdf_bytes,
                            sparse_after_dpi_retry,
                            dpi=300,
                            psm=4,
                            crop_to_dominant_image=True,
                            progress_callback=on_progress,
                        )
                        adaptive_ocr_ms += _elapsed_ms(crop_retry_started)
                        for page_number in sparse_after_dpi_retry:
                            first = direct_stats[page_number]
                            retry = cropped_stats[page_number]
                            if (
                                int(retry["character_count"])
                                > int(first["character_count"]) * 1.05
                                or float(retry["mean_confidence"])
                                > float(first["mean_confidence"]) + 2.0
                            ):
                                direct_pages[page_number] = cropped_pages[page_number]
                                direct_stats[page_number] = retry

                    confidences = [
                        float(stats["mean_confidence"])
                        for stats in direct_stats.values()
                        if int(stats["word_count"]) > 0
                    ]
                    direct_mean_confidence = (
                        round(sum(confidences) / len(confidences), 2)
                        if confidences
                        else 0.0
                    )
                    ocr_ms = primary_ocr_ms + adaptive_ocr_ms
                    geometry = merge_geometry_pages(
                        preflight_geometry,
                        direct_pages,
                    )
                    direct_summary = summarize_geometry_quality(geometry)
                    unresolved_direct_pages = sorted(
                        set(direct_ocr_page_numbers)
                        & set(direct_summary["garbled_page_numbers"])
                    )
                    if unresolved_direct_pages:
                        raise RuntimeError(
                            "Direct OCR remained garbled on page(s) "
                            + ",".join(map(str, unresolved_direct_pages))
                        )
                    _emit_success(geometry)
                    return
                except Exception as exc:  # noqa: BLE001 — preserve legacy path
                    direct_ocr_error = str(exc)
                    on_progress(
                        "Direct OCR unavailable; using compatibility pipeline…"
                    )
            else:
                on_progress(
                    "Ruled table detected; using compatibility OCR pipeline…"
                )

        selected_profile = PROFILE_DEFAULT
        evaluated_profiles = [PROFILE_DEFAULT]
        ocr_ran = True
        if preflight_garbled:
            initial_summary = preflight_summary
            initial_characters = initial_summary["total_characters"]
            initial_non_whitespace_characters = initial_summary[
                "non_whitespace_characters"
            ]
            initial_alphanumeric_ratio = initial_summary["alphanumeric_ratio"]
            initial_garbled_page_numbers = list(
                initial_summary["garbled_page_numbers"]
            )
            forced_page_numbers = initial_garbled_page_numbers
            escalation_reason = "text-garbled-preflight"
            ocr_mode = MODE_FORCE
            on_progress(
                "Source text mapping is garbled on page(s) "
                f"{','.join(map(str, forced_page_numbers))}; "
                "skipping redo and rasterizing only those pages…"
            )
            ocr_started = time.perf_counter()
            try:
                result_bytes = ocr_pdf_bytes(
                    pdf_bytes,
                    mode=MODE_FORCE,
                    auto_rotate_pages=not job.preserve_source_pdf,
                    pages=",".join(map(str, forced_page_numbers)),
                    progress_callback=on_progress,
                )
            finally:
                primary_ocr_ms = _elapsed_ms(ocr_started)
        else:
            # Rung 1 is redo: it replaces the invisible text layer and leaves
            # page content alone. Rung 2 force is used only if redo reveals an
            # unusable layer or rejects the input.
            on_progress("Starting OCR…")
            ocr_started = time.perf_counter()
            ocr_mode = MODE_REDO
            try:
                result_bytes = ocr_pdf_bytes(
                    pdf_bytes,
                    mode=MODE_REDO,
                    auto_rotate_pages=not job.preserve_source_pdf,
                    progress_callback=on_progress,
                )
                primary_ocr_ms = _elapsed_ms(ocr_started)
            except DigitalSignatureError:
                primary_ocr_ms = _elapsed_ms(ocr_started)
                # Raised before the mode is consulted, so force cannot help either.
                raise
            except InputFileError as exc:
                primary_ocr_ms = _elapsed_ms(ocr_started)
                # redo refuses some inputs outright — notably AcroForm PDFs.
                on_progress(
                    f"Redo mode rejected this PDF ({exc}); rasterizing instead…"
                )
                escalation_reason = "input-rejected"
                ocr_mode = MODE_FORCE
                forced_page_numbers = list(range(1, page_count + 1))
                fallback_started = time.perf_counter()
                try:
                    result_bytes = ocr_pdf_bytes(
                        pdf_bytes,
                        mode=MODE_FORCE,
                        auto_rotate_pages=not job.preserve_source_pdf,
                        progress_callback=on_progress,
                    )
                finally:
                    fallback_ocr_ms = _elapsed_ms(fallback_started)
                    ocr_ms = primary_ocr_ms + fallback_ocr_ms
            except Exception:
                primary_ocr_ms = _elapsed_ms(ocr_started)
                ocr_ms = primary_ocr_ms
                raise
        ocr_ms = primary_ocr_ms + fallback_ocr_ms

        geometry_started = time.perf_counter()
        geometry = extract_text_geometry(result_bytes, progress_callback=on_progress)
        primary_geometry_ms = _elapsed_ms(geometry_started)
        geometry_ms = primary_geometry_ms
        if not preflight_garbled:
            initial_summary = summarize_geometry_quality(geometry)
            initial_characters = initial_summary["total_characters"]
            initial_non_whitespace_characters = initial_summary[
                "non_whitespace_characters"
            ]
            initial_alphanumeric_ratio = initial_summary["alphanumeric_ratio"]
            initial_garbled_page_numbers = list(
                initial_summary["garbled_page_numbers"]
            )

        # Rung 2: redo either left us no characters or dense punctuation garbage
        # from a broken Type0/Identity-H character map. Both require rendering
        # visible glyphs and recognizing their appearance rather than trusting
        # the PDF's unusable text mapping.
        garbled_text = needs_garbled_text_retry(initial_summary)
        if (
            (initial_characters == 0 or garbled_text)
            and geometry["pages"]
            and ocr_mode == MODE_REDO
        ):
            if garbled_text:
                forced_page_numbers = initial_garbled_page_numbers
                on_progress(
                    "Redo text appears garbled on page(s) "
                    f"{','.join(map(str, forced_page_numbers))}; "
                    "rasterizing only those pages…"
                )
                escalation_reason = "text-garbled"
            else:
                forced_page_numbers = list(range(1, page_count + 1))
                on_progress("Redo produced no extractable text; rasterizing instead…")
                escalation_reason = "text-unextractable"
            ocr_mode = MODE_FORCE

            ocr_started = time.perf_counter()
            try:
                result_bytes = ocr_pdf_bytes(
                    result_bytes,
                    mode=MODE_FORCE,
                    auto_rotate_pages=not job.preserve_source_pdf,
                    pages=",".join(map(str, forced_page_numbers)) or None,
                    progress_callback=on_progress,
                )
            finally:
                fallback_ocr_ms += _elapsed_ms(ocr_started)
                ocr_ms = primary_ocr_ms + fallback_ocr_ms + adaptive_ocr_ms

            geometry_started = time.perf_counter()
            geometry = extract_text_geometry(result_bytes, progress_callback=on_progress)
            fallback_geometry_ms += _elapsed_ms(geometry_started)
            geometry_ms = primary_geometry_ms + fallback_geometry_ms

        current_summary = summarize_geometry_quality(geometry)

        # ── Adaptive layout retry ─────────────────────────────────────────────
        # A low-DPI image is only a reason to spend on adaptive OCR when the
        # resulting text is also weak. Previously, one low-resolution image
        # triggered three profile evaluations even for dense, high-quality text.
        adaptive_detection_started = time.perf_counter()
        sparse_result = needs_adaptive_retry(current_summary)
        low_resolution_scan = needs_high_resolution_retry(
            pdf_bytes,
            progress_callback=on_progress,
        )
        low_resolution_quality_risk = needs_low_resolution_quality_retry(
            current_summary,
            low_resolution_scan,
        )
        adaptive_detection_ms = _elapsed_ms(adaptive_detection_started)
        if (
            (sparse_result or low_resolution_quality_risk)
            and geometry["pages"]
            and not legacy_table_required
        ):
            try:
                if sparse_result:
                    on_progress("OCR coverage low; evaluating high-resolution layouts…")
                else:
                    on_progress(
                        "Low-resolution scan detected; evaluating high-resolution layouts…"
                    )
                evaluation_started = time.perf_counter()
                try:
                    evaluations = evaluate_adaptive_profiles(
                        pdf_bytes,
                        progress_callback=on_progress,
                    )
                finally:
                    adaptive_evaluation_ms = _elapsed_ms(evaluation_started)
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
                try:
                    retry_page_numbers = (
                        forced_page_numbers
                        if 0 < len(forced_page_numbers) < page_count
                        else []
                    )
                    retry_source_bytes = (
                        result_bytes if retry_page_numbers else pdf_bytes
                    )
                    retry_bytes = ocr_pdf_bytes(
                        retry_source_bytes,
                        mode=ocr_mode,
                        auto_rotate_pages=not job.preserve_source_pdf,
                        tesseract_pagesegmode=candidate_options[
                            "tesseract_pagesegmode"
                        ],
                        oversample=candidate_options["oversample"],
                        pages=(
                            ",".join(map(str, retry_page_numbers))
                            if retry_page_numbers
                            else None
                        ),
                        progress_callback=on_progress,
                    )
                finally:
                    adaptive_ocr_ms = _elapsed_ms(retry_started)
                    ocr_ms = primary_ocr_ms + fallback_ocr_ms + adaptive_ocr_ms

                retry_geometry_started = time.perf_counter()
                retry_geometry = extract_text_geometry(
                    retry_bytes, progress_callback=on_progress
                )
                adaptive_geometry_ms = _elapsed_ms(retry_geometry_started)
                geometry_ms = (
                    primary_geometry_ms
                    + fallback_geometry_ms
                    + adaptive_geometry_ms
                )
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
                    if low_resolution_quality_risk and not escalation_reason:
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
            table_images_examined = int(date_stats["table_images_examined"])
            table_images_skipped_small = int(
                date_stats["table_images_skipped_small"]
            )
            table_grid_candidates = int(date_stats["table_grid_candidates"])
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
                table_geometry_ms = _elapsed_ms(corrected_geometry_started)
                geometry_ms = (
                    primary_geometry_ms
                    + fallback_geometry_ms
                    + adaptive_geometry_ms
                    + table_geometry_ms
                )
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

        # result_bytes is derived exclusively from the already-scrubbed pdf_bytes
        # and locally generated OCR text. A second scrub repeats the expensive PDF
        # rewrite without removing any additional active content.
        on_progress("Transferring OCR result…")
        _emit_success(geometry, result_bytes, result_is_sanitized=True)
    except Exception as exc:  # noqa: BLE001
        _write(
            OcrResult(
                job_id=job.job_id,
                status="error",
                error=str(exc),
                diagnostics=_diagnostics(page_count),
            ).to_dict()
        )


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
