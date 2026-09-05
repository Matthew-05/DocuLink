"""Score fs-value detection: the span oracle, and a per-document report.

With no arguments this runs the span oracle -- the unit-level gate that pins the
exact spans a line of text must produce. Given a PDF it runs the detector over
real geometry and reports what was published, what was suppressed and why, so
two runs can be compared across a change.

    py scripts/score_fs_values.py
    py scripts/score_fs_values.py "test-imports.local/apple 10k.pdf" --write-report output/fs.json
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT_ROOT = Path(__file__).resolve().parent
for entry in (str(ROOT / "src" / "python"), str(SCRIPT_ROOT)):
    if entry not in sys.path:
        sys.path.insert(0, entry)

from engines.fs_values.spans import recognize_spans  # noqa: E402


ORACLE = ROOT / "src" / "python" / "tests" / "fixtures" / "fs_values" / "span-oracle.json"

# A suppressor must never discard a *well-formed value* carrying an unambiguous
# financial mark. Anything caught here is a recall bug, not a tuning question.
#
# The recognizer's own refusals are exempt: it rejects whole tokens, and a token
# like "5,200-acre" or "10-K," carries a comma without ever having been a value.
_STRONG_MARKS = "$€£¥₹₩,%"
_NON_VALUE_NOISE_REASONS = frozenset({
    "identifier",
    "alphanumeric",
    "partial-token",
    "note-header",
    "note-reference",
})


def _span_dict(span) -> dict:
    item = {"kind": span.kind, "text": span.text}
    if span.normalized_value:
        item["normalizedValue"] = span.normalized_value
    if span.currency:
        item["currency"] = span.currency
    if span.magnitude:
        item["magnitude"] = span.magnitude
    if span.date_precision:
        item["datePrecision"] = span.date_precision
    if span.date_order == "ambiguous":
        item["dateOrder"] = span.date_order
    return item


def run_oracle() -> int:
    cases = json.loads(ORACLE.read_text(encoding="utf-8"))
    passed = 0
    for case in cases:
        actual = [_span_dict(span) for span in recognize_spans(case["text"])]
        if actual == case["spans"]:
            passed += 1
        else:
            print(json.dumps({"text": case["text"], "expected": case["spans"], "actual": actual}))
    print(json.dumps({"cases": len(cases), "passed": passed, "failed": len(cases) - passed}))
    return 0 if passed == len(cases) else 1


def score_document(pdf: Path) -> dict:
    from table_corpus import geometry_for

    from engines.fs_values.detector import DETECTOR_VERSION, detect_fs_values

    diagnostics: dict = {}
    model = detect_fs_values(geometry_for(pdf.read_bytes()), diagnostics=diagnostics)
    published = [value for page in model["pages"] for value in page["values"]]
    rejected = [item for page in model["pages"] for item in page.get("noise", [])]
    page_count = len(model["pages"]) or 1
    suspicious = [
        candidate
        for candidate in rejected
        if candidate["reason"] not in _NON_VALUE_NOISE_REASONS
        and any(mark in candidate["text"] for mark in _STRONG_MARKS)
    ]
    return {
        "document": pdf.name,
        "detectorVersion": DETECTOR_VERSION,
        "pages": len(model["pages"]),
        "published": len(published),
        "publishedPerPage": round(len(published) / page_count, 2),
        "rejected": len(rejected),
        "recognized": len(published) + len(rejected),
        "publishedByKind": dict(Counter(value["kind"] for value in published)),
        "rejectedByReason": dict(Counter(candidate["reason"] for candidate in rejected)),
        "notes": len(model.get("notes", [])),
        "noteReferences": len(model.get("noteReferences", [])),
        "rejectedCarryingFinancialMarks": [
            {"reason": candidate["reason"], "text": candidate["text"]} for candidate in suspicious
        ],
        "diagnostics": {key: value for key, value in sorted(diagnostics.items())},
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf", nargs="*", type=Path, help="score these documents instead of the oracle")
    parser.add_argument("--write-report", type=Path)
    args = parser.parse_args()

    if not args.pdf:
        return run_oracle()

    reports = [score_document(pdf) for pdf in args.pdf]
    for report in reports:
        print(json.dumps(report, indent=2, ensure_ascii=False))
    if args.write_report is not None:
        args.write_report.parent.mkdir(parents=True, exist_ok=True)
        args.write_report.write_text(
            json.dumps(reports if len(reports) > 1 else reports[0], indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
    return 1 if any(report["rejectedCarryingFinancialMarks"] for report in reports) else 0


if __name__ == "__main__":
    raise SystemExit(main())
