"""
DocuLink OCR worker — stdin/stdout JSON-line protocol.

The C# host spawns this process once and sends newline-delimited JSON jobs
via stdin. For each job this worker writes one or more progress lines followed
by exactly one success/error result line, all to stdout.

Protocol is defined in contracts/python-worker-v1.json.
"""
from __future__ import annotations

import base64
import json
import sys

from engines.geometry_engine import extract_text_geometry, geometry_to_base64
from engines.ocr_engine import ocr_pdf_bytes
from schemas.models import OcrJob, OcrProgress, OcrResult


def _write(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def _handle_job(job: OcrJob) -> None:
    def on_progress(message: str) -> None:
        _write(OcrProgress(job_id=job.job_id, message=message).to_dict())

    try:
        pdf_bytes = base64.b64decode(job.pdf_base64)

        if job.mode == "geometry-only":
            geometry = extract_text_geometry(pdf_bytes, progress_callback=on_progress)
            geometry_b64 = geometry_to_base64(geometry)
            _write(
                OcrResult(
                    job_id=job.job_id,
                    status="success",
                    geometry_base64=geometry_b64,
                ).to_dict()
            )
            return

        on_progress("Starting OCR…")
        result_bytes = ocr_pdf_bytes(pdf_bytes, progress_callback=on_progress)
        geometry = extract_text_geometry(result_bytes, progress_callback=on_progress)
        geometry_b64 = geometry_to_base64(geometry)
        result_b64 = base64.b64encode(result_bytes).decode("ascii")
        _write(
            OcrResult(
                job_id=job.job_id,
                status="success",
                pdf_base64=result_b64,
                geometry_base64=geometry_b64,
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
            job = OcrJob.from_dict(data)
        except (json.JSONDecodeError, KeyError) as exc:
            # Emit an error without a real job_id so the host can log it.
            _write({"job_id": "", "status": "error", "error": f"Invalid job: {exc}"})
            continue

        _handle_job(job)


if __name__ == "__main__":
    main()
