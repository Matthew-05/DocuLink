using System.Collections.Generic;
using System.Text;
using DocuLink.Addin.Modules.CustomXml.Models;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>
    /// Serializes outbound host→viewer messages to JSON strings that conform to
    /// contracts/webview-messages-v1.json.
    /// </summary>
    internal static class HostMessageSerializer
    {
        /// <summary>Returns the JSON payload for a <c>pdfs-loaded</c> message.</summary>
        public static string BuildPdfsLoaded(IList<PdfDocument> pdfs)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"pdfs-loaded\",\"pdfs\":[");

            for (int i = 0; i < pdfs.Count; i++)
            {
                PdfDocument pdf = pdfs[i];
                if (i > 0) sb.Append(',');

                sb.Append('{');
                sb.Append("\"id\":"); AppendString(sb, pdf.Id);
                sb.Append(",\"name\":"); AppendString(sb, pdf.Name ?? string.Empty);
                sb.Append(",\"base64\":"); AppendString(sb, pdf.Base64 ?? string.Empty);
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Returns the JSON payload for a <c>linked-rectangles-loaded</c> message.
        /// </summary>
        public static string BuildLinkedRectanglesLoaded(IList<LinkedRectangle> rects)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"linked-rectangles-loaded\",\"rectangles\":[");

            for (int i = 0; i < rects.Count; i++)
            {
                LinkedRectangle r = rects[i];
                if (i > 0) sb.Append(',');

                sb.Append('{');
                sb.Append("\"id\":"); AppendString(sb, r.Id);
                sb.Append(",\"pdfId\":"); AppendString(sb, r.PdfId);
                sb.Append(",\"page\":"); sb.Append(r.Rectangle.PageIndex);
                sb.Append(",\"rect\":{");
                sb.Append("\"x\":"); AppendDouble(sb, r.Rectangle.X);
                sb.Append(",\"y\":"); AppendDouble(sb, r.Rectangle.Y);
                sb.Append(",\"width\":"); AppendDouble(sb, r.Rectangle.Width);
                sb.Append(",\"height\":"); AppendDouble(sb, r.Rectangle.Height);
                sb.Append("}}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Returns the JSON payload for a <c>clear-rectangle-highlight</c> message.</summary>
        public static string BuildClearRectangleHighlight() =>
            "{\"type\":\"clear-rectangle-highlight\"}";

        /// <summary>
        /// Returns the JSON payload for a <c>navigate-to-rectangle</c> message.
        /// </summary>
        public static string BuildNavigateToRectangle(string id, string pdfId, int page)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"navigate-to-rectangle\"");
            sb.Append(",\"id\":"); AppendString(sb, id ?? string.Empty);
            sb.Append(",\"pdfId\":"); AppendString(sb, pdfId ?? string.Empty);
            sb.Append(",\"page\":"); sb.Append(page);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendDouble(StringBuilder sb, double value)
        {
            sb.Append(value.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void AppendString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:x4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
