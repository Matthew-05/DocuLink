"""Data models for the DocuLink Python worker protocol (python-worker-v1.json)."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class OcrJob:
    """Inbound job received from the C# host via stdin."""
    job_id: str
    command: str
    pdf_base64: str
    mode: str = "full"

    @staticmethod
    def from_dict(d: dict) -> "OcrJob":
        mode = d.get("mode", "full")
        if mode not in ("full", "geometry-only"):
            mode = "full"
        return OcrJob(
            job_id=d["job_id"],
            command=d["command"],
            pdf_base64=d["pdf_base64"],
            mode=mode,
        )


@dataclass
class OcrResult:
    """Outbound result written to stdout."""
    job_id: str
    status: str          # "success" | "error"
    pdf_base64: str = "" # populated on full OCR success
    geometry_base64: str = ""
    table_structure_base64: str = ""
    error: str = ""      # populated on error
    # OcrDiagnostics per contracts/python-worker-v1.json. Host debug logging
    # only — the host must tolerate this being absent and must not branch on it.
    diagnostics: dict = None

    def to_dict(self) -> dict:
        d: dict = {"job_id": self.job_id, "status": self.status}
        if self.status == "success":
            if self.pdf_base64:
                d["pdf_base64"] = self.pdf_base64
            if self.geometry_base64:
                d["geometry_base64"] = self.geometry_base64
            if self.table_structure_base64:
                d["table_structure_base64"] = self.table_structure_base64
            if self.diagnostics:
                d["diagnostics"] = self.diagnostics
        else:
            d["error"] = self.error
            if self.diagnostics:
                d["diagnostics"] = self.diagnostics
        return d


@dataclass
class ConvertJob:
    """Inbound conversion job received from the C# host via stdin."""
    job_id: str
    command: str
    source_base64: str
    source_extension: str
    source_name: str = ""

    @staticmethod
    def from_dict(d: dict) -> "ConvertJob":
        return ConvertJob(
            job_id=d["job_id"],
            command=d["command"],
            source_base64=d["source_base64"],
            source_extension=d["source_extension"],
            source_name=d.get("source_name", ""),
        )


@dataclass
class ConvertResult:
    """Outbound conversion result written to stdout.

    output_kind is "pdf" when the worker produced finished PDF bytes, or "html"
    when it could only normalise the source (e.g. .eml) and the host must render
    the markup to PDF itself.
    """
    job_id: str
    status: str            # "success" | "error"
    output_kind: str = ""  # "pdf" | "html"
    pdf_base64: str = ""
    html: str = ""
    error: str = ""

    def to_dict(self) -> dict:
        if self.status != "success":
            return {"job_id": self.job_id, "status": "error", "error": self.error}

        d: dict = {
            "job_id": self.job_id,
            "status": "success",
            "output_kind": self.output_kind,
        }
        if self.output_kind == "pdf":
            d["pdf_base64"] = self.pdf_base64
        else:
            d["html"] = self.html
        return d


@dataclass
class OcrProgress:
    """Intermediate progress message written to stdout during processing."""
    job_id: str
    message: str

    def to_dict(self) -> dict:
        return {"job_id": self.job_id, "status": "progress", "message": self.message}
