import unittest

from engines.ocr_progress_plugin import (
    DocuLinkProgressBar,
    configure_progress_callback,
    progress_message_with_elapsed,
)


class OcrProgressPluginTests(unittest.TestCase):
    def tearDown(self) -> None:
        configure_progress_callback(None)

    def test_reports_only_completed_pages_for_fractional_updates(self) -> None:
        messages: list[str] = []
        configure_progress_callback(messages.append)

        with DocuLinkProgressBar(total=3, desc="OCR", unit="page") as progress:
            progress.update(0.5)
            progress.update(0.5)
            progress.update(1)

        self.assertEqual(
            messages,
            [
                "OCR page 0 of 3…",
                "OCR page 1 of 3…",
                "OCR page 2 of 3…",
                "OCR page 3 of 3…",
            ],
        )

    def test_heartbeat_retains_latest_determinate_count(self) -> None:
        configure_progress_callback(lambda _message: None)
        with DocuLinkProgressBar(
            total=4,
            desc="Scanning contents",
            unit="page",
        ) as progress:
            progress.update()
            message = progress_message_with_elapsed("12s")

        self.assertEqual(
            message,
            "Inspecting PDF page 1 of 4 — 12s elapsed…",
        )

    def test_unit_scale_is_applied_to_total_and_updates(self) -> None:
        messages: list[str] = []
        configure_progress_callback(messages.append)

        with DocuLinkProgressBar(
            total=6,
            desc="hOCR",
            unit="page",
            unit_scale=0.5,
        ) as progress:
            for _ in range(6):
                progress.update()

        self.assertEqual(messages[-1], "Recognizing text page 3 of 3…")


if __name__ == "__main__":
    unittest.main()
