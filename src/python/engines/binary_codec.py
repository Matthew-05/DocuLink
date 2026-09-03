"""Compact JSON transport encoding shared by OCR-adjacent engines."""
from __future__ import annotations

import base64
import gzip
import json


def json_to_base64(value: dict) -> str:
    """Serialize compact JSON, gzip it, and return its base64 representation."""
    payload = json.dumps(value, separators=(",", ":")).encode("utf-8")
    return base64.b64encode(gzip.compress(payload)).decode("ascii")
