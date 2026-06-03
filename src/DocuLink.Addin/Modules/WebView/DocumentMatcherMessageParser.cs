using System;
using System.Collections.Generic;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>
    /// Parses inbound document-matcher→host JSON messages.
    /// </summary>
    internal static class DocumentMatcherMessageParser
    {
        public static string GetMessageType(string json) =>
            WebMessageParser.GetMessageType(json);

        public static string ParseMatcherLog(string json)
        {
            var dict = Deserialize(json);
            return GetStringOrEmpty(dict, "message");
        }

        public static MatcherGeometryPreparedPayload ParseMatcherGeometryPrepared(string json)
        {
            var dict = Deserialize(json);
            return new MatcherGeometryPreparedPayload
            {
                PdfId          = GetString(dict, "pdfId"),
                GeometryBase64 = GetString(dict, "geometryBase64"),
            };
        }

        public static StartMatchingPayload ParseStartMatching(string json)
        {
            var dict = Deserialize(json);

            var outputColNumbersRaw = dict["outputColNumbers"] as System.Collections.ArrayList;
            if (outputColNumbersRaw == null)
                throw new FormatException("start-matching missing 'outputColNumbers' array.");

            var outputColNumbers = new List<int>();
            foreach (var item in outputColNumbersRaw)
                outputColNumbers.Add(ToInt(item));

            var folderIdsRaw = dict["folderIds"] as System.Collections.ArrayList;
            var folderIds = new List<string>();
            if (folderIdsRaw != null)
            {
                foreach (var item in folderIdsRaw)
                {
                    if (item is string s && !string.IsNullOrWhiteSpace(s))
                        folderIds.Add(s);
                }
            }

            return new StartMatchingPayload { OutputColNumbers = outputColNumbers, FolderIds = folderIds };
        }

        public static CreateLinksPayload ParseCreateLinks(string json)
        {
            var dict = Deserialize(json);

            var linksRaw = dict["links"] as System.Collections.ArrayList;
            if (linksRaw == null)
                throw new FormatException("create-links missing 'links' array.");

            var links = new List<LinkCreationEntry>();
            foreach (var item in linksRaw)
            {
                var entry = item as Dictionary<string, object>;
                if (entry == null) continue;

                var rectRaw = entry["rect"] as Dictionary<string, object>;
                if (rectRaw == null) continue;

                links.Add(new LinkCreationEntry
                {
                    RowIndex        = GetInt(entry, "rowIndex"),
                    OutputColNumber = GetInt(entry, "outputColNumber"),
                    PdfId           = GetString(entry, "pdfId"),
                    PageIndex       = GetInt(entry, "pageIndex"),
                    RectX           = GetDouble(rectRaw, "x"),
                    RectY           = GetDouble(rectRaw, "y"),
                    RectWidth       = GetDouble(rectRaw, "width"),
                    RectHeight      = GetDouble(rectRaw, "height"),
                    Text            = GetStringOrEmpty(entry, "text"),
                });
            }

            return new CreateLinksPayload { Links = links };
        }

        private static Dictionary<string, object> Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON must be non-empty.", nameof(json));

            var dict = WebMessageParser.Serializer.Deserialize<Dictionary<string, object>>(json);
            if (dict == null)
                throw new FormatException("Could not parse JSON object.");
            return dict;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out object val) && val is string s)
                return s;
            throw new FormatException($"Missing or non-string field '{key}'.");
        }

        private static string GetStringOrEmpty(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out object val) && val is string s)
                return s;
            return string.Empty;
        }

        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out object val))
                return ToInt(val);
            throw new FormatException($"Missing or non-numeric field '{key}'.");
        }

        private static int ToInt(object val)
        {
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is double d) return (int)d;
            if (val is decimal m) return (int)m;
            throw new FormatException($"Cannot convert '{val}' to int.");
        }

        private static double GetDouble(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out object val))
            {
                if (val is double d) return d;
                if (val is int i) return i;
                if (val is long l) return l;
                if (val is decimal m) return (double)m;
            }
            throw new FormatException($"Missing or non-numeric field '{key}'.");
        }
    }

    // ── Payload DTOs ──────────────────────────────────────────────────────────

    internal sealed class StartMatchingPayload
    {
        /// <summary>
        /// 1-based Excel column numbers for the output cell of each key column,
        /// in the same order as the key columns sent in <c>matcher-ready</c>.
        /// </summary>
        public List<int>    OutputColNumbers { get; set; }
        public List<string> FolderIds        { get; set; }
    }

    internal sealed class LinkCreationEntry
    {
        public int    RowIndex        { get; set; }
        /// <summary>1-based Excel column number of the target output cell.</summary>
        public int    OutputColNumber { get; set; }
        public string PdfId          { get; set; }
        public int    PageIndex       { get; set; }
        public double RectX           { get; set; }
        public double RectY           { get; set; }
        public double RectWidth       { get; set; }
        public double RectHeight      { get; set; }
        public string Text            { get; set; }
    }

    internal sealed class CreateLinksPayload
    {
        public List<LinkCreationEntry> Links { get; set; }
    }

    internal sealed class MatcherGeometryPreparedPayload
    {
        public string PdfId          { get; set; }
        public string GeometryBase64 { get; set; }
    }
}
