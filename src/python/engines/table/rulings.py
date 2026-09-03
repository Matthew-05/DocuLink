"""Raster and vector ruling-line detection."""
from __future__ import annotations

from PIL import Image, ImageOps
import math


_GRID_DARKNESS_LEVELS = (110, 140, 170, 200, 220)
_MIN_IMAGE_WIDTH = 400
_MIN_IMAGE_HEIGHT = 200


def _pixel_values(image: Image.Image) -> list[int]:
    flattened = getattr(image, "get_flattened_data", None)
    return list(flattened() if flattened is not None else image.getdata())


def _line_centers(
    counts: list[int],
    minimum: int,
    *,
    merge_distance: int = 1,
) -> list[int]:
    runs: list[list[int]] = []
    for index, count in enumerate(counts):
        if count < minimum:
            continue
        if not runs or index > runs[-1][-1] + merge_distance:
            runs.append([index])
        else:
            runs[-1].append(index)
    centers: list[int] = []
    for run in runs:
        weights = [counts[index] for index in run]
        total_weight = sum(weights)
        centers.append(round(sum(index * weight for index, weight in zip(run, weights)) / max(1, total_weight)))
    return centers


def detect_ruled_grid(image: Image.Image) -> tuple[list[int], list[int]]:
    """Return strong full-table vertical and horizontal line centers in pixels."""
    gray = ImageOps.grayscale(image)
    width, height = gray.size
    if width < _MIN_IMAGE_WIDTH or height < _MIN_IMAGE_HEIGHT:
        return [], []
    for darkness in _GRID_DARKNESS_LEVELS:
        dark = gray.point(lambda value, limit=darkness: 255 if value < limit else 0)
        vertical_density = _pixel_values(dark.resize((width, 1), Image.Resampling.BOX))
        horizontal_density = _pixel_values(dark.resize((1, height), Image.Resampling.BOX))
        x_lines = _line_centers(
            vertical_density,
            round(255 * 0.70),
            merge_distance=max(1, round(width * 0.008)),
        )
        y_lines = _line_centers(
            horizontal_density,
            round(255 * 0.50),
            merge_distance=max(1, round(height * 0.008)),
        )
        if len(x_lines) >= 3 and len(y_lines) >= 3:
            return x_lines, y_lines
        # A photographed or deskewed page can contain continuous rules that drift
        # several pixels along their length. Detect them in local strips and cluster
        # the strip coordinates; unlike a blur, this does not turn page edges and
        # large text into full-length rules.
        x_lines = _strip_projected_lines(dark, vertical=True)
        y_lines = _strip_projected_lines(dark, vertical=False)
        if len(x_lines) >= 3 and len(y_lines) >= 3:
            return x_lines, y_lines
    return [], []


def _strip_projected_lines(dark: Image.Image, *, vertical: bool) -> list[int]:
    width, height = dark.size
    segment_count = 16
    axis_size = width if vertical else height
    cross_size = height if vertical else width
    tolerance = max(2, round(axis_size * 0.018))
    clusters: list[dict] = []
    for segment in range(segment_count):
        start = round(cross_size * segment / segment_count)
        end = round(cross_size * (segment + 1) / segment_count)
        crop = dark.crop((0, start, width, end)) if vertical else dark.crop((start, 0, end, height))
        projected = crop.resize((axis_size, 1) if vertical else (1, axis_size), Image.Resampling.BOX)
        centers = _line_centers(
            _pixel_values(projected),
            round(255 * 0.58),
            merge_distance=max(1, round(axis_size * 0.003)),
        )
        for center in centers:
            if center < axis_size * 0.02 or center > axis_size * 0.98:
                continue
            nearest = min(
                clusters,
                key=lambda cluster: abs(center - sum(cluster["values"]) / len(cluster["values"])),
                default=None,
            )
            if nearest is None or abs(center - sum(nearest["values"]) / len(nearest["values"])) > tolerance:
                clusters.append({"values": [center], "segments": {segment}})
            else:
                nearest["values"].append(center)
                nearest["segments"].add(segment)
    # A line-item grid near the bottom of an invoice may occupy only a quarter
    # of the page height. Horizontal rules still need broad page-width support,
    # while vertical rules may legitimately appear in fewer cross-axis strips.
    support_ratio = 0.18 if vertical else 0.35
    minimum_support = max(3, math.ceil(segment_count * support_ratio))
    centers = [
        round(sum(cluster["values"]) / len(cluster["values"]))
        for cluster in clusters
        if len(cluster["segments"]) >= minimum_support
    ]
    return _line_centers(
        [255 if index in centers else 0 for index in range(axis_size)],
        1,
        merge_distance=max(1, round(axis_size * 0.012)),
    )


def _dedupe(values: list[float], tolerance: float = 0.004) -> list[float]:
    result: list[float] = []
    for value in sorted(max(0.0, min(1.0, item)) for item in values):
        if not result or value - result[-1] > tolerance:
            result.append(value)
        else:
            result[-1] = (result[-1] + value) / 2
    return result


def _vector_rulings(page) -> tuple[list[float], list[float]]:
    width = float(page.rect.width)
    height = float(page.rect.height)
    if width <= 0 or height <= 0:
        return [], []
    vertical: list[float] = []
    horizontal: list[float] = []
    for drawing in page.get_drawings():
        for item in drawing.get("items", []):
            kind = item[0]
            segments = []
            if kind == "l" and len(item) >= 3:
                segments.append((item[1], item[2]))
            elif kind == "re" and len(item) >= 2:
                rect = item[1]
                segments.extend(
                    [
                        ((rect.x0, rect.y0), (rect.x1, rect.y0)),
                        ((rect.x1, rect.y0), (rect.x1, rect.y1)),
                        ((rect.x1, rect.y1), (rect.x0, rect.y1)),
                        ((rect.x0, rect.y1), (rect.x0, rect.y0)),
                    ]
                )
            for first, second in segments:
                x0, y0 = float(first[0]), float(first[1])
                x1, y1 = float(second[0]), float(second[1])
                if abs(x1 - x0) <= 1.5 and abs(y1 - y0) >= height * 0.04:
                    vertical.append(((x0 + x1) / 2 - page.rect.x0) / width)
                if abs(y1 - y0) <= 1.5 and abs(x1 - x0) >= width * 0.04:
                    horizontal.append(((y0 + y1) / 2 - page.rect.y0) / height)
    return _dedupe(vertical), _dedupe(horizontal)


def detect_page_rulings(page, *, dpi: int = 150) -> tuple[list[float], list[float]]:
    """Return normalized displayed-page rulings from vector paths and raster projection."""
    vertical, horizontal = _vector_rulings(page)
    try:
        pixmap = page.get_pixmap(dpi=dpi, alpha=False)
        image = Image.frombytes("RGB", (pixmap.width, pixmap.height), pixmap.samples)
        x_lines, y_lines = detect_ruled_grid(image)
        vertical.extend(value / pixmap.width for value in x_lines)
        horizontal.extend(value / pixmap.height for value in y_lines)
    except Exception:  # Raster evidence is opportunistic; vector evidence remains useful.
        pass
    return _dedupe(vertical), _dedupe(horizontal)
