using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DocuLink.Addin.Modules.CustomXml.Models;
using DocuLink.Addin.Modules.Services;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>
    /// Serializes outbound host→file-manager messages to JSON strings that conform to
    /// contracts/webview-messages-v1.json (FilesLoadedMessage).
    /// Base64 bytes are never included; only metadata is sent.
    /// </summary>
    internal static class FileManagerMessageSerializer
    {
        public static string BuildFilesLoaded(IList<PdfFolder> folders, IList<PdfMetadata> pdfs)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"files-loaded\",\"folders\":[");

            for (int i = 0; i < folders.Count; i++)
            {
                if (i > 0) sb.Append(',');
                PdfFolder f = folders[i];
                sb.Append('{');
                sb.Append("\"id\":"); AppendString(sb, f.Id);
                sb.Append(",\"name\":"); AppendString(sb, f.Name ?? string.Empty);
                sb.Append('}');
            }

            sb.Append("],\"files\":[");

            for (int i = 0; i < pdfs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                PdfMetadata p = pdfs[i];

                string dateAdded = p.DateAdded.HasValue
                    ? p.DateAdded.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                    : string.Empty;

                sb.Append('{');
                sb.Append("\"id\":"); AppendString(sb, p.Id);
                sb.Append(",\"name\":"); AppendString(sb, p.Name ?? string.Empty);
                if (!string.IsNullOrEmpty(p.FolderId))
                {
                    sb.Append(",\"folderId\":"); AppendString(sb, p.FolderId);
                }
                sb.Append(",\"status\":"); AppendString(sb, !string.IsNullOrWhiteSpace(p.OcrStatus) ? p.OcrStatus : PdfStatus.None);
                sb.Append(",\"fileSizeBytes\":"); sb.Append(p.FileSizeBytes.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"dateAdded\":"); AppendString(sb, dateAdded);
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Builds a host→web <c>ocr-status</c> message for a single PDF.
        /// message is optional — pass null to omit it.
        /// </summary>
        public static string BuildOcrStatus(string pdfId, string status, string message = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"ocr-status\",\"pdfId\":");
            AppendString(sb, pdfId ?? string.Empty);
            sb.Append(",\"status\":");
            AppendString(sb, status ?? string.Empty);
            if (!string.IsNullOrEmpty(message))
            {
                sb.Append(",\"message\":");
                AppendString(sb, message);
            }
            sb.Append('}');
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
