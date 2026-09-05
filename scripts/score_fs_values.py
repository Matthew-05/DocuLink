"""Run the fs-value span oracle and print a compact diagnostic summary."""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src" / "python"))

from engines.fs_values.spans import recognize_spans  # noqa: E402


def main() -> int:
    fixture = ROOT / "src" / "python" / "tests" / "fixtures" / "fs_values" / "span-oracle.json"
    cases = json.loads(fixture.read_text(encoding="utf-8"))
    passed = 0
    for case in cases:
        actual = [(span.kind, span.text) for span in recognize_spans(case["text"])]
        expected = [(span["kind"], span["text"]) for span in case["spans"]]
        if actual == expected:
            passed += 1
        else:
            print(json.dumps({"text": case["text"], "expected": expected, "actual": actual}))
    print(json.dumps({"cases": len(cases), "passed": passed, "failed": len(cases) - passed}))
    return 0 if passed == len(cases) else 1


if __name__ == "__main__":
    raise SystemExit(main())
