"""Score detected table structure against table-structure-v1 page goldens.

The scorer is the acceptance gate for detector changes, so it measures the things
that actually went wrong in the field rather than only boundary positions:

* tables are matched to goldens by area overlap, never by array position, so a
  missed first table cannot shift every later comparison;
* extra predictions cost precision, and a golden page may legitimately expect
  zero tables — that is how a chart or a bullet list is regression-tested;
* one prediction covering two goldens is counted as an over-merge, and two
  predictions covering one golden as an over-split;
* row and column boundaries are scored only inside matched pairs;
* header row counts are compared;
* runtime is recorded per document and per page.

Goldens are hand-approved. The scorer never blesses its own output.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from table_corpus import PYTHON_ROOT, detect, geometry_for


def _bounds_tuple(bounds: dict) -> tuple[float, float, float, float]:
    x = float(bounds["x"])
    y = float(bounds["y"])
    return x, y, x + float(bounds["width"]), y + float(bounds["height"])


def _intersection(first: dict, second: dict) -> float:
    ax0, ay0, ax1, ay1 = _bounds_tuple(first)
    bx0, by0, bx1, by1 = _bounds_tuple(second)
    return max(0.0, min(ax1, bx1) - max(ax0, bx0)) * max(0.0, min(ay1, by1) - max(ay0, by0))


def _area(bounds: dict) -> float:
    return float(bounds["width"]) * float(bounds["height"])


def iou(first: dict, second: dict) -> float:
    overlap = _intersection(first, second)
    union = _area(first) + _area(second) - overlap
    return overlap / union if union > 0 else 0.0


def covers(outer: dict, inner: dict) -> float:
    """How much of `inner` the `outer` box covers."""
    area = _area(inner)
    return _intersection(outer, inner) / area if area > 0 else 0.0


def match_tables(expected: list[dict], predicted: list[dict], threshold: float):
    """Greedy highest-overlap matching between goldens and predictions."""
    pairs = sorted(
        (
            (iou(exp["bounds"], pred["bounds"]), exp_index, pred_index)
            for exp_index, exp in enumerate(expected)
            for pred_index, pred in enumerate(predicted)
        ),
        key=lambda item: (-item[0], item[1], item[2]),
    )
    matched: list[tuple[int, int, float]] = []
    used_expected: set[int] = set()
    used_predicted: set[int] = set()
    for score, exp_index, pred_index in pairs:
        if score < threshold:
            break
        if exp_index in used_expected or pred_index in used_predicted:
            continue
        used_expected.add(exp_index)
        used_predicted.add(pred_index)
        matched.append((exp_index, pred_index, score))
    return matched, used_expected, used_predicted


def _positions(table: dict, axis: str) -> list[float]:
    bands = table["columns"] if axis == "column" else table["rows"]
    end_key = "x1" if axis == "column" else "y1"
    return [float(band[end_key]) for band in bands[:-1]]


def _boundary_counts(expected: list[float], actual: list[float], tolerance: float):
    unmatched = list(actual)
    hits = 0
    for target in expected:
        if not unmatched:
            break
        distance, index = min(
            (abs(value - target), index) for index, value in enumerate(unmatched)
        )
        if distance <= tolerance:
            hits += 1
            unmatched.pop(index)
    return hits, len(actual), len(expected)


def _ratio(hits: int, total: int, *, empty_is_perfect: bool) -> float:
    if total:
        return hits / total
    return 1.0 if empty_is_perfect else 0.0


def _f1(precision: float, recall: float) -> float:
    return 2 * precision * recall / (precision + recall) if precision + recall else 0.0


def _header_rows(table: dict) -> int:
    header = table.get("header")
    return int(header["rowCount"]) if header else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf", type=Path)
    parser.add_argument("--goldens", type=Path, default=PYTHON_ROOT / "tests" / "fixtures" / "tables")
    parser.add_argument("--write-candidates", type=Path)
    parser.add_argument("--write-report", type=Path)
    parser.add_argument("--tolerance", type=float, default=0.01, help="boundary tolerance")
    parser.add_argument("--iou", type=float, default=0.5, help="table match threshold")
    parser.add_argument(
        "--periods",
        action="store_true",
        help="run the alpha period analysis, which is off by default",
    )
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    pdf_bytes = args.pdf.read_bytes()
    geometry = geometry_for(pdf_bytes)
    structure, diagnostics, elapsed = detect(
        pdf_bytes, geometry, periods=args.periods or None
    )
    page_count = max(1, len(structure["pages"]))

    if args.write_candidates:
        args.write_candidates.mkdir(parents=True, exist_ok=True)

    totals = {
        "expected": 0,
        "predicted": 0,
        "matched": 0,
        "overMerged": 0,
        "overSplit": 0,
        "headerScored": 0,
        "headerCorrect": 0,
    }
    boundaries = {
        axis: {"hits": 0, "predicted": 0, "expected": 0} for axis in ("column", "row")
    }
    page_reports: list[dict] = []
    golden_pages = 0

    for page in structure["pages"]:
        number = page["pageIndex"] + 1
        filename = f"{args.pdf.stem}.page-{number}.json"
        if args.write_candidates:
            (args.write_candidates / filename).write_text(
                json.dumps(page, indent=2) + "\n", encoding="utf-8"
            )
        golden_path = args.goldens / filename
        if not golden_path.exists():
            continue
        golden_pages += 1
        expected = json.loads(golden_path.read_text(encoding="utf-8")).get("tables", [])
        predicted = page["tables"]
        matched, used_expected, used_predicted = match_tables(expected, predicted, args.iou)

        totals["expected"] += len(expected)
        totals["predicted"] += len(predicted)
        totals["matched"] += len(matched)
        for table in predicted:
            if sum(1 for exp in expected if covers(table["bounds"], exp["bounds"]) >= 0.5) >= 2:
                totals["overMerged"] += 1
        for exp in expected:
            if sum(1 for table in predicted if covers(exp["bounds"], table["bounds"]) >= 0.5) >= 2:
                totals["overSplit"] += 1

        for exp_index, pred_index, _score in matched:
            for axis in ("column", "row"):
                hits, predicted_count, expected_count = _boundary_counts(
                    _positions(expected[exp_index], axis),
                    _positions(predicted[pred_index], axis),
                    args.tolerance,
                )
                boundaries[axis]["hits"] += hits
                boundaries[axis]["predicted"] += predicted_count
                boundaries[axis]["expected"] += expected_count
            totals["headerScored"] += 1
            if _header_rows(expected[exp_index]) == _header_rows(predicted[pred_index]):
                totals["headerCorrect"] += 1

        page_reports.append(
            {
                "page": number,
                "expected": len(expected),
                "predicted": len(predicted),
                "matched": len(matched),
                "missed": [
                    expected[index]["bounds"]
                    for index in range(len(expected))
                    if index not in used_expected
                ],
                "extra": [
                    {"bounds": predicted[index]["bounds"], "confidence": predicted[index]["confidence"]}
                    for index in range(len(predicted))
                    if index not in used_predicted
                ],
            }
        )

    precision = _ratio(totals["matched"], totals["predicted"], empty_is_perfect=totals["expected"] == 0)
    recall = _ratio(totals["matched"], totals["expected"], empty_is_perfect=True)
    report = {
        "document": args.pdf.name,
        "detectorVersion": diagnostics.get("table_detector_version", ""),
        "tolerance": args.tolerance,
        "iouThreshold": args.iou,
        "goldenPages": golden_pages,
        "pageCount": len(structure["pages"]),
        "runtime": {
            "detectionSeconds": round(elapsed, 3),
            "msPerPage": round(elapsed * 1000 / page_count, 2),
        },
        "tables": {
            "expected": totals["expected"],
            "predicted": totals["predicted"],
            "matched": totals["matched"],
            "precision": round(precision, 4),
            "recall": round(recall, 4),
            "f1": round(_f1(precision, recall), 4),
        },
        "boundaries": {
            axis: {
                "precision": round(
                    _ratio(values["hits"], values["predicted"], empty_is_perfect=values["expected"] == 0), 4
                ),
                "recall": round(
                    _ratio(values["hits"], values["expected"], empty_is_perfect=values["predicted"] == 0), 4
                ),
                "f1": round(
                    _f1(
                        _ratio(values["hits"], values["predicted"], empty_is_perfect=values["expected"] == 0),
                        _ratio(values["hits"], values["expected"], empty_is_perfect=values["predicted"] == 0),
                    ),
                    4,
                ),
            }
            for axis, values in boundaries.items()
        },
        "headers": {
            "scored": totals["headerScored"],
            "correct": totals["headerCorrect"],
            "accuracy": round(
                _ratio(totals["headerCorrect"], totals["headerScored"], empty_is_perfect=True), 4
            ),
        },
        "structure": {"overMerged": totals["overMerged"], "overSplit": totals["overSplit"]},
        "diagnostics": diagnostics,
        "pages": page_reports,
    }

    if args.write_report:
        args.write_report.parent.mkdir(parents=True, exist_ok=True)
        args.write_report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    if golden_pages == 0:
        print(
            "No goldens scored. Use --write-candidates, inspect them, then copy approved "
            f"pages into {args.goldens}."
        )
        return 0
    if not args.quiet:
        for page in report["pages"]:
            if page["expected"] != page["matched"] or page["predicted"] != page["matched"]:
                print(
                    f"page {page['page']}: expected {page['expected']} predicted "
                    f"{page['predicted']} matched {page['matched']}"
                )
        tables = report["tables"]
        print(
            f"tables P={tables['precision']:.3f} R={tables['recall']:.3f} F1={tables['f1']:.3f} "
            f"({tables['matched']}/{tables['expected']} over {golden_pages} golden pages)"
        )
        for axis in ("column", "row"):
            values = report["boundaries"][axis]
            print(
                f"{axis} boundaries P={values['precision']:.3f} R={values['recall']:.3f} "
                f"F1={values['f1']:.3f}"
            )
        print(
            f"headers {report['headers']['correct']}/{report['headers']['scored']} "
            f"({report['headers']['accuracy']:.3f}); over-merged {totals['overMerged']}, "
            f"over-split {totals['overSplit']}"
        )
        print(f"runtime {report['runtime']['detectionSeconds']}s "
              f"({report['runtime']['msPerPage']} ms/page)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
