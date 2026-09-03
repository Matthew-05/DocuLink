"""Group text-geometry characters into source lines and measure their bounds.

Pure geometry over the text-geometry-v1 character shape: no OCR, PDF or Excel
APIs. Kept out of ocr_engine so the table engine can use it without importing
the OCR runtime.
"""
from __future__ import annotations


def line_groups(page: dict) -> list[list[dict]]:
    groups: dict[int, list[dict]] = {}
    order: list[int] = []
    for character in page.get("characters", []):
        line_index = int(character.get("lineIndex", 0))
        if line_index not in groups:
            groups[line_index] = []
            order.append(line_index)
        groups[line_index].append(character)
    return [groups[line_index] for line_index in order]


def line_bounds(characters: list[dict]) -> tuple[float, float, float, float] | None:
    visible = [
        character
        for character in characters
        if str(character.get("char", "")).strip()
        and float(character.get("width", 0.0)) > 0
        and float(character.get("height", 0.0)) > 0
    ]
    if not visible:
        return None
    return (
        min(float(character["x"]) for character in visible),
        min(float(character["y"]) for character in visible),
        max(
            float(character["x"]) + float(character["width"])
            for character in visible
        ),
        max(
            float(character["y"]) + float(character["height"])
            for character in visible
        ),
    )
