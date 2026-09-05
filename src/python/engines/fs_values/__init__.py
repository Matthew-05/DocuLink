"""Financial-statement value recognition over text-geometry-v1."""

from .detector import DETECTOR_VERSION, detect_fs_values, fs_values_to_base64

__all__ = ["DETECTOR_VERSION", "detect_fs_values", "fs_values_to_base64"]
