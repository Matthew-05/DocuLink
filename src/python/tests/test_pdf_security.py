from __future__ import annotations

import unittest

import pymupdf

from engines.pdf_security import sanitize_pdf_bytes


class PdfSecurityTests(unittest.TestCase):
    def test_sanitize_removes_embedded_files_and_links_but_keeps_text(self) -> None:
        document = pymupdf.open()
        page = document.new_page()
        page.insert_text((72, 72), "Talliark visible content")
        page.insert_link(
            {
                "kind": pymupdf.LINK_URI,
                "from": pymupdf.Rect(60, 50, 220, 80),
                "uri": "https://example.invalid/payload",
            }
        )
        document.embfile_add("payload.txt", b"passive test attachment")
        source = document.tobytes()
        document.close()

        result = sanitize_pdf_bytes(source)

        self.assertEqual(1, result.removed_embedded_files)
        self.assertEqual(1, result.removed_links)

        normalized = pymupdf.open(stream=result.pdf_bytes, filetype="pdf")
        try:
            self.assertEqual(0, normalized.embfile_count())
            self.assertEqual([], normalized[0].get_links())
            self.assertIn("Talliark visible content", normalized[0].get_text())
        finally:
            normalized.close()

    def test_sanitize_rejects_empty_input(self) -> None:
        with self.assertRaisesRegex(ValueError, "empty"):
            sanitize_pdf_bytes(b"")


if __name__ == "__main__":
    unittest.main()
