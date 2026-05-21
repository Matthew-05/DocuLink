"""Data models for the DocuLink Python worker protocol (python-worker-v1.json)."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass
class OcrJob:
    """Inbound job received from the C# host via stdin."""
    job_id: str
    command: str
    pdf_base64: str

    @staticmethod
    def from_dict(d: dict) -> "OcrJob":
        return OcrJob(
            job_id=d["job_id"],
            command=d["command"],
            pdf_base64=d["pdf_base64"],
        )


@dataclass
class OcrResult:
    """Outbound result written to stdout."""
    job_id: str
    status: str          # "success" | "error"
    pdf_base64: str = "" # populated on success
    error: str = ""      # populated on error

    def to_dict(self) -> dict:
        d: dict = {"job_id": self.job_id, "status": self.status}
        if self.status == "success":
            d["pdf_base64"] = self.pdf_base64
        else:
            d["error"] = self.error
        return d


@dataclass
class OcrProgress:
    """Intermediate progress message written to stdout during processing."""
    job_id: str
    message: str

    def to_dict(self) -> dict:
        return {"job_id": self.job_id, "status": "progress", "message": self.message}
