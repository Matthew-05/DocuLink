using System;
using System.Text;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Lightweight C# heuristic to distinguish PDFs with an embedded text layer from scanned image-only PDFs.
    /// Used when a PDF is first added to assign status "text" or "none". Does not invoke the Python worker.
    /// </summary>
    internal static class PdfTextLayerDetector
    {
        private const int ScanWindowBytes = 2 * 1024 * 1024;
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        /// <summary>Returns "text" when the PDF likely has selectable text, otherwise "none".</summary>
        public static string ClassifyFromBase64(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                return "none";

            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                return ContainsExtractableText(bytes) ? "text" : "none";
            }
            catch
            {
                return "none";
            }
        }

        private static bool ContainsExtractableText(byte[] pdf)
        {
            return HasTextLayerMarkers(GetScanContent(pdf));
        }

        /// <summary>
        /// Reads the first and last scan windows. Many PDFs store font dictionaries and the xref table
        /// near the end of the file, so head-only scanning misses WinAnsi/TrueType text layers.
        /// </summary>
        private static string GetScanContent(byte[] pdf)
        {
            if (pdf.Length <= ScanWindowBytes)
                return Latin1.GetString(pdf);

            string head = Latin1.GetString(pdf, 0, ScanWindowBytes);

            if (pdf.Length <= ScanWindowBytes * 2)
                return Latin1.GetString(pdf);

            string tail = Latin1.GetString(pdf, pdf.Length - ScanWindowBytes, ScanWindowBytes);
            return head + tail;
        }

        private static bool HasTextLayerMarkers(string content)
        {
            if (HasToUnicodeFont(content))
                return true;

            if (HasFontWithStandardEncoding(content))
                return true;

            if (HasUncompressedTextOperators(content))
                return true;

            return false;
        }

        private static bool HasToUnicodeFont(string content)
        {
            return content.IndexOf("/ToUnicode", StringComparison.Ordinal) >= 0
                && content.IndexOf("/Font", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Native text PDFs often use standard font encodings without a ToUnicode CMap
        /// (e.g. TrueType + WinAnsiEncoding).
        /// </summary>
        private static bool HasFontWithStandardEncoding(string content)
        {
            if (content.IndexOf("/Font", StringComparison.Ordinal) < 0)
                return false;

            if (content.IndexOf("/WinAnsiEncoding", StringComparison.Ordinal) >= 0
                || content.IndexOf("/MacRomanEncoding", StringComparison.Ordinal) >= 0
                || content.IndexOf("/PDFDocEncoding", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            return content.IndexOf("/BaseFont", StringComparison.Ordinal) >= 0
                && (content.IndexOf("/TrueType", StringComparison.Ordinal) >= 0
                    || content.IndexOf("/Type1", StringComparison.Ordinal) >= 0);
        }

        /// <summary>
        /// Fallback for PDFs with uncompressed content streams.
        /// </summary>
        private static bool HasUncompressedTextOperators(string content)
        {
            if (content.IndexOf(" BT", StringComparison.Ordinal) < 0
                && content.IndexOf("\nBT", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            return content.IndexOf(" Tj", StringComparison.Ordinal) >= 0
                || content.IndexOf(" TJ", StringComparison.Ordinal) >= 0
                || content.IndexOf("\nTj", StringComparison.Ordinal) >= 0
                || content.IndexOf("\nTJ", StringComparison.Ordinal) >= 0;
        }
    }
}
