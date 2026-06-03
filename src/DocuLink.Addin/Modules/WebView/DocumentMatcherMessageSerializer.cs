using System.Collections.Generic;
using System.Text;
using DocuLink.Addin.Modules.CustomXml.Models;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>
    /// Serializes outbound host→document-matcher JSON messages.
    /// </summary>
    internal static class DocumentMatcherMessageSerializer
    {
        /// <summary>
        /// Sent on load: communicates the key columns derived from the Excel selection,
        /// the available output columns that follow the rightmost key column, and the folder list.
        /// </summary>
        public static string BuildMatcherReady(
            int rowCount,
            IList<KeyColumnEntry> keyColumns,
            IList<OutputColumnEntry> outputColumns,
            IList<PdfFolder> folders)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"matcher-ready\"");
            sb.Append(",\"rowCount\":"); sb.Append(rowCount);

            sb.Append(",\"keyColumns\":[");
            for (int i = 0; i < keyColumns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var kc = keyColumns[i];
                sb.Append("{\"colNumber\":"); sb.Append(kc.ColNumber);
                sb.Append(",\"header\":"); AppendString(sb, kc.Header ?? string.Empty);
                sb.Append(",\"rangeAddress\":"); AppendString(sb, kc.RangeAddress ?? string.Empty);
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append(",\"outputColumns\":[");
            for (int i = 0; i < outputColumns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var oc = outputColumns[i];
                sb.Append("{\"colNumber\":"); sb.Append(oc.ColNumber);
                sb.Append(",\"header\":"); AppendString(sb, oc.Header ?? string.Empty);
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append(",\"folders\":[");
            for (int i = 0; i < folders.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":"); AppendString(sb, folders[i].Id ?? string.Empty);
                sb.Append(",\"name\":"); AppendString(sb, folders[i].Name ?? string.Empty);
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Sent after the user clicks Start: provides the key column values per data row
        /// and the geometry data for all searchable PDFs in the selected folders.
        /// </summary>
        public static string BuildMatcherDataLoaded(
            IList<MatcherRowEntry> rows,
            IList<MatcherPdfEntry> pdfs)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"matcher-data-loaded\"");

            sb.Append(",\"rows\":[");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var row = rows[i];
                sb.Append("{\"rowIndex\":"); sb.Append(row.RowIndex);
                sb.Append(",\"keyValues\":[");
                for (int j = 0; j < row.KeyValues.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    AppendString(sb, row.KeyValues[j] ?? string.Empty);
                }
                sb.Append("]}");
            }
            sb.Append(']');

            sb.Append(",\"pdfs\":[");
            for (int i = 0; i < pdfs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var pdf = pdfs[i];
                sb.Append("{\"id\":"); AppendString(sb, pdf.Id ?? string.Empty);
                sb.Append(",\"name\":"); AppendString(sb, pdf.Name ?? string.Empty);
                sb.Append(",\"folderId\":"); AppendString(sb, pdf.FolderId ?? string.Empty);
                sb.Append(",\"geometryBase64\":");
                if (pdf.GeometryBase64 != null)
                    AppendString(sb, pdf.GeometryBase64);
                else
                    sb.Append("null");
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Sent after all <c>create-links</c> entries have been processed.
        /// </summary>
        public static string BuildLinksCreated(IList<LinkResultEntry> results)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"links-created\",\"results\":[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = results[i];
                sb.Append("{\"rowIndex\":"); sb.Append(r.RowIndex);
                sb.Append(",\"outputColNumber\":"); sb.Append(r.OutputColNumber);
                sb.Append(",\"success\":"); sb.Append(r.Success ? "true" : "false");
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

    // ── Serialization DTOs ────────────────────────────────────────────────────

    internal sealed class KeyColumnEntry
    {
        public int    ColNumber    { get; set; }
        public string Header       { get; set; }
        public string RangeAddress { get; set; }
    }

    internal sealed class OutputColumnEntry
    {
        public int    ColNumber { get; set; }
        public string Header    { get; set; }
    }

    internal sealed class MatcherRowEntry
    {
        public int          RowIndex  { get; set; }
        public List<string> KeyValues { get; set; }
    }

    internal sealed class MatcherPdfEntry
    {
        public string Id             { get; set; }
        public string Name           { get; set; }
        public string FolderId       { get; set; }
        public string GeometryBase64 { get; set; }
    }

    internal sealed class LinkResultEntry
    {
        public int  RowIndex       { get; set; }
        public int  OutputColNumber { get; set; }
        public bool Success        { get; set; }
    }
}
