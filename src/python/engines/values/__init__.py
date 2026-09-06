"""Value, reference and noise recognition over text-geometry-v1."""

from .detector import DETECTOR_VERSION, detect_values, values_to_base64

__all__ = ["DETECTOR_VERSION", "detect_values", "values_to_base64"]
