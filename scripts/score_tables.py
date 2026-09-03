"""Score table row/column boundaries against table-structure-v1 page goldens."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
PYTHON_ROOT = REPO_ROOT / "src" / "python"
sys.path.insert(0, str(PYTHON_ROOT))

from engines.geometry_engine import extract_text_geometry  # noqa: E402
from engines.ocr_engine import extract_direct_text_geometry  # noqa: E402
from engines.table.detector import detect_tables  # noqa: E402


def _positions(table: dict, axis: str) -> list[float]:
    bands = table["columns"] if axis == "column" else table["rows"]
    end_key = "x1" if axis == "column" else "y1"
    return [float(band[end_key]) for band in bands[:-1]]


def _score(expected: list[float], actual: list[float], tolerance: float) -> tuple[float, float]:
    unmatched = list(actual)
    matches = 0
    for target in expected:
        candidates = [(abs(value - target), index) for index, value in enumerate(unmatched)]
        if not candidates:
            continue
        distance, index = min(candidates)
        if distance <= tolerance:
            matches += 1
            unmatched.pop(index)
    precision = matches / len(actual) if actual else (1.0 if not expected else 0.0)
    recall = matches / len(expected) if expected else (1.0 if not actual else 0.0)
    return precision, recall


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("pdf", type=Path)
    parser.add_argument("--goldens", type=Path, default=PYTHON_ROOT / "tests" / "fixtures" / "tables")
    parser.add_argument("--write-candidates", type=Path)
    parser.add_argument("--tolerance", type=float, default=0.01)
    args = parser.parse_args()

    pdf_bytes = args.pdf.read_bytes()
    geometry = extract_text_geometry(pdf_bytes)
    sparse_pages = [
        page["pageIndex"] + 1
        for page in geometry["pages"]
        if sum(bool(str(char.get("char", "")).strip()) for char in page["characters"]) < 20
    ]
    if sparse_pages:
        ocr_pages, _ = extract_direct_text_geometry(pdf_bytes, sparse_pages, dpi=300, psm=6)
        replacements = {page_number - 1: page for page_number, page in ocr_pages.items()}
        geometry["pages"] = [replacements.get(page["pageIndex"], page) for page in geometry["pages"]]
    model = detect_tables(pdf_bytes, geometry)
    if args.write_candidates:
        args.write_candidates.mkdir(parents=True, exist_ok=True)

    scored = 0
    for page in model["pages"]:
        filename = f"{args.pdf.stem}.page-{page['pageIndex'] + 1}.json"
        if args.write_candidates:
            (args.write_candidates / filename).write_text(
                json.dumps(page, indent=2) + "\n", encoding="utf-8"
            )
        golden_path = args.goldens / filename
        if not golden_path.exists():
            print(f"page {page['pageIndex'] + 1}: no golden ({golden_path})")
            continue
        expected_page = json.loads(golden_path.read_text(encoding="utf-8"))
        for index, expected in enumerate(expected_page.get("tables", [])):
            if index >= len(page["tables"]):
                print(f"page {page['pageIndex'] + 1} table {index + 1}: missing")
                continue
            actual = page["tables"][index]
            cp, cr = _score(_positions(expected, "column"), _positions(actual, "column"), args.tolerance)
            rp, rr = _score(_positions(expected, "row"), _positions(actual, "row"), args.tolerance)
            print(
                f"page {page['pageIndex'] + 1} table {index + 1}: "
                f"columns P={cp:.2f} R={cr:.2f}; rows P={rp:.2f} R={rr:.2f}"
            )
            scored += 1
    if scored == 0:
        print("No goldens scored. Use --write-candidates, inspect them, then copy approved pages into the golden folder.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
