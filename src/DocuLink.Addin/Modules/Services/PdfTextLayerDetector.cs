using System;
using System.Text;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Lightweight heuristic to distinguish PDFs with an embedded text layer from scanned image-only PDFs.
    /// Used when a PDF is first added to assign status "text" or "none".
    /// </summary>
    internal static class PdfTextLayerDetector
    {
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
            // Scan the first 2 MB — sufficient for typical PDF headers/catalog/early pages.
            int length = Math.Min(pdf.Length, 2 * 1024 * 1024);
            string content = Encoding.GetEncoding("ISO-8859-1").GetString(pdf, 0, length);

            if (content.IndexOf("/ToUnicode", StringComparison.Ordinal) >= 0
                && content.IndexOf("/Font", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (content.IndexOf(" BT", StringComparison.Ordinal) >= 0)
            {
                if (content.IndexOf(" Tj", StringComparison.Ordinal) >= 0
                    || content.IndexOf(" TJ", StringComparison.Ordinal) >= 0
                    || content.IndexOf("\nTj", StringComparison.Ordinal) >= 0
                    || content.IndexOf("\nTJ", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
