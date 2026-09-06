"""Geometry builders and a two-tier detection helper shared by the engine tests.

The pipeline publishes two models from one reading of a document. `detect`
returns both, joined into one mapping so an assertion can reach a value and the
note catalogue without knowing which artifact carried it; `values_model` and
`structure_model` are there for the tests that care about the separation.
"""
from __future__ import annotations

from engines.fs.detector import detect_fs_structure
from engines.values.detector import detect_values
from engines.values.lines import prepare


class Detected(dict):
    """The two published models, joined for assertion convenience."""

    values_model: dict
    structure_model: dict
    diagnostics: dict


def line(text: str, *, y: float, line_index: int, height: float = 0.012, x: float = 0.02) -> list[dict]:
    return [
        {
            "char": char,
            "x": x + index * 0.008,
            "y": y,
            "width": 0.008,
            "height": height,
            "lineIndex": line_index,
        }
        for index, char in enumerate(text)
    ]


def cell(text: str, *, y: float, line_index: int, x: float, height: float = 0.012) -> list[dict]:
    return line(text, y=y, line_index=line_index, height=height, x=x)


def geometry(pages: list[list[dict]]) -> dict:
    return {
        "version": 1,
        "coordinateSpace": "normalized",
        "pages": [
            {"pageIndex": index, "characters": characters}
            for index, characters in enumerate(pages)
        ],
    }


def detect(model: dict, *, tables: dict | None = None) -> Detected:
    """Run both tiers over one text-geometry model."""
    document = prepare(model)
    structure = detect_fs_structure(document, tables=tables)
    diagnostics: dict = {}
    values = detect_values(document, claims=structure.spans, diagnostics=diagnostics)

    joined = Detected(values)
    joined.update(
        {
            "documentClass": structure.model["documentClass"],
            "apparatus": structure.model["apparatus"],
            "notes": structure.model["notes"],
            "noteReferences": structure.model["noteReferences"],
            "items": structure.model["items"],
            "itemReferences": structure.model["itemReferences"],
        }
    )
    joined.values_model = values
    joined.structure_model = structure.model
    joined.diagnostics = diagnostics
    return joined


def detect_pages(pages: list[list[dict]]) -> Detected:
    return detect(geometry(pages))


def published(model: dict) -> list[str]:
    return [value["text"] for page in model["pages"] for value in page["values"]]


def references(model: dict) -> list[dict]:
    return [item for page in model["pages"] for item in page.get("references", [])]


def structure(model: dict) -> list[dict]:
    return [item for page in model["pages"] for item in page.get("structure", [])]


def noise(model: dict) -> list[dict]:
    return [item for page in model["pages"] for item in page.get("noise", [])]


def refused(model: dict) -> list[dict]:
    """Everything the detector declined to publish as a value or a reference."""
    return structure(model) + noise(model)


def label(item: dict) -> str:
    """What a refusal called itself: a noise reason, or a structure kind."""
    return item.get("reason") or item["kind"]
