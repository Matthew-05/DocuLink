"""Financial-document structure: the apparatus a filing or statement indexes itself by."""

from .detector import DETECTOR_VERSION, detect_fs_structure, structure_to_base64

__all__ = ["DETECTOR_VERSION", "detect_fs_structure", "structure_to_base64"]
