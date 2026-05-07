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
