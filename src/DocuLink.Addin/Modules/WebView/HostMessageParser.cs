using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using DocuLink.Addin.Modules.CustomXml.Models;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>
    /// Parses inbound viewer→host messages that conform to
    /// contracts/webview-messages-v1.json.
    /// </summary>
    internal static class HostMessageParser
    {
        /// <inheritdoc cref="WebMessageParser.GetMessageType"/>
        public static string GetMessageType(string json) =>
            WebMessageParser.GetMessageType(json);

    /// <summary>
    /// Parses a <c>link-rectangle-clicked</c> message and returns the
    /// rectangle id, or <c>null</c> on failure.
    /// </summary>
    public static string ParseLinkRectangleClicked(string json)
    {
        return ParseRectangleIdMessage(json);
    }

    /// <summary>
    /// Parses a <c>link-rectangle-deleted</c> message and returns the
    /// rectangle id, or <c>null</c> on failure.
    /// </summary>
    public static string ParseLinkRectangleDeleted(string json)
    {
        return ParseRectangleIdMessage(json);
    }

    /// <summary>Parses the deletion mode as well as the rectangle id.</summary>
    public static LinkRectangleDeletedPayload ParseLinkRectangleDeletion(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var obj = WebMessageParser.Serializer.Deserialize<Dictionary<string, object>>(json);
            if (obj == null) return null;

            string id = obj.TryGetValue("id", out object idValue) ? idValue as string : null;
            if (string.IsNullOrWhiteSpace(id)) return null;

            return new LinkRectangleDeletedPayload
            {
                Id = id,
                DeleteCellData = obj.ContainsKey("deleteCellData")
                    && ParseBoolean(obj, "deleteCellData"),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses a request to copy a Table rectangle to one or more pages.</summary>
    public static CopyTableSelectionPayload ParseCopyTableSelection(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var obj = WebMessageParser.Serializer.Deserialize<Dictionary<string, object>>(json);
            if (obj == null) return null;

            string id = obj.TryGetValue("id", out object idValue) ? idValue as string : null;
            if (string.IsNullOrWhiteSpace(id)
                || !obj.TryGetValue("targets", out object targetsValue)
                || !(targetsValue is IEnumerable targets)
                || targetsValue is string)
                return null;

            var parsedTargets = new List<TableSelectionCopyTargetPayload>();
            foreach (object targetValue in targets)
            {
                if (!(targetValue is Dictionary<string, object> target)
                    || !target.TryGetValue("page", out object pageValue))
                    continue;

                ParseTableGrid(
                    target,
                    out TableGrid tableGrid,
                    out IList<IList<string>> tableCells);
                if (tableGrid == null || tableCells == null) continue;

                parsedTargets.Add(new TableSelectionCopyTargetPayload
                {
                    Page = Convert.ToInt32(pageValue),
                    TableGrid = tableGrid,
                    TableCells = tableCells,
                });
            }

            return parsedTargets.Count == 0
                ? null
                : new CopyTableSelectionPayload { Id = id, Targets = parsedTargets };
        }
        catch
        {
            return null;
        }
    }

    private static string ParseRectangleIdMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var obj = WebMessageParser.Serializer.Deserialize<Dictionary<string, object>>(json);
            if (obj == null) return null;

            return obj.TryGetValue("id", out object idVal) ? idVal as string : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an <c>excel-navigate</c> message. Returns <c>null</c> when the message is
    /// malformed or names a motion this host does not implement.
    /// </summary>
    public static ExcelNavigatePayload ParseExcelNavigate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var obj = WebMessageParser.Serializer.Deserialize<Dictionary<string, object>>(json);
            if (obj == null) return null;

            if (!obj.TryGetValue("motion", out object motionVal) || !(motionVal is string motion))
                return null;

            Services.ExcelCellNavigationService.Motion parsed;
            if (string.Equals(motion, "tab", StringComparison.OrdinalIgnoreCase))
                parsed = Services.ExcelCellNavigationService.Motion.Tab;
            else if (string.Equals(motion, "enter", StringComparison.OrdinalIgnoreCase))
                parsed = Services.ExcelCellNavigationService.Motion.Enter;
            else
                return null;

            return new ExcelNavigatePayload
            {
                Motion  = parsed,
                Reverse = ParseBoolean(obj, "reverse"),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a <c>link-rectangle-created</c> message into a
    /// <see cref="LinkRectangleCreatedPayload"/>. Returns <c>null</c> on failure.
    /// </summary>
    public static LinkRectangleCreatedPayload ParseLinkRectangleCreated(string json)
        {
            return ParseLinkRectangleWithText(json, includeId: false) as LinkRectangleCreatedPayload;
        }

    /// <summary>
    /// Parses a <c>link-rectangle-updated</c> message into a
    /// <see cref="LinkRectangleUpdatedPayload"/>. Returns <c>null</c> on failure.
    /// </summary>
    public static LinkRectangleUpdatedPayload ParseLinkRectangleUpdated(string json)
        {
            return ParseLinkRectangleWithText(json, includeId: true) as LinkRectangleUpdatedPayload;
        }

    private static object ParseLinkRectangleWithText(string json, bool includeId)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var obj = WebMessageParser.Serializer.Deserialize<Dictionary<string, object>>(json);
                if (obj == null) return null;

                string id    = obj.TryGetValue("id",    out object idVal)  ? idVal as string : null;
                string pdfId = obj.TryGetValue("pdfId", out object pidVal) ? pidVal as string : null;
                int    page  = obj.TryGetValue("page",  out object pgVal)  ? Convert.ToInt32(pgVal) : 0;
                string text  = obj.TryGetValue("text",  out object txtVal) ? (txtVal as string ?? "") : "";
                LinkType linkType = ParseLinkType(obj);
                bool appendToActiveSum = ParseBoolean(obj, "appendToActiveSum");
                ParseTableGrid(obj, out TableGrid tableGrid, out IList<IList<string>> tableCells);

                if (includeId && string.IsNullOrWhiteSpace(id))
                    return null;

                double rx = 0, ry = 0, rw = 0, rh = 0;
                if (obj.TryGetValue("rect", out object rectVal)
                    && rectVal is Dictionary<string, object> rect)
                {
                    rx = rect.TryGetValue("x",      out object xv) ? Convert.ToDouble(xv) : 0;
                    ry = rect.TryGetValue("y",      out object yv) ? Convert.ToDouble(yv) : 0;
                    rw = rect.TryGetValue("width",  out object wv) ? Convert.ToDouble(wv) : 0;
                    rh = rect.TryGetValue("height", out object hv) ? Convert.ToDouble(hv) : 0;
                }

                if (includeId)
                {
                    return new LinkRectangleUpdatedPayload
                    {
                        Id     = id,
                        PdfId  = pdfId,
                        Page   = page,
                        X      = rx,
                        Y      = ry,
                        Width  = rw,
                        Height = rh,
                        Text   = text,
                        TableGrid = tableGrid,
                        TableCells = tableCells,
                    };
                }

                return new LinkRectangleCreatedPayload
                {
                    PdfId    = pdfId,
                    Page     = page,
                    X        = rx,
                    Y        = ry,
                    Width    = rw,
                    Height   = rh,
                    Text     = text,
                    LinkType = linkType,
                    AppendToActiveSum = appendToActiveSum,
                    TableGrid = tableGrid,
                    TableCells = tableCells,
                };
            }
            catch
            {
                return null;
            }
        }

    private static LinkType ParseLinkType(Dictionary<string, object> obj)
    {
        if (!obj.TryGetValue("linkType", out object ltVal) || !(ltVal is string ltStr))
            return LinkType.Auto;
        if (string.Equals(ltStr, "raw", StringComparison.OrdinalIgnoreCase)) return LinkType.Raw;
        if (string.Equals(ltStr, "sum", StringComparison.OrdinalIgnoreCase)) return LinkType.Sum;
        if (string.Equals(ltStr, "table", StringComparison.OrdinalIgnoreCase)) return LinkType.Table;
        return LinkType.Auto;
    }

    private static void ParseTableGrid(
        Dictionary<string, object> obj,
        out TableGrid tableGrid,
        out IList<IList<string>> tableCells)
    {
        tableGrid = null;
        tableCells = null;
        if (!obj.TryGetValue("table", out object tableValue)
            || !(tableValue is Dictionary<string, object> tableObject))
            return;

        tableGrid = new TableGrid
        {
            ColumnBoundaries = ParseBoundaries(tableObject, "columnBoundaries"),
            RowBoundaries = ParseBoundaries(tableObject, "rowBoundaries"),
        };

        if (tableObject.TryGetValue("cells", out object cellsValue))
            tableCells = ParseTableCells(cellsValue);
    }

    private static IList<IList<string>> ParseTableCells(object cellsValue)
    {
        if (!(cellsValue is IEnumerable rows) || cellsValue is string)
            return null;

        var parsedRows = new List<IList<string>>();
        foreach (object rowValue in rows)
        {
            if (!(rowValue is IEnumerable values) || rowValue is string)
                return null;
            parsedRows.Add(values.Cast<object>()
                .Select(value => value?.ToString() ?? string.Empty)
                .ToList());
        }
        return parsedRows;
    }

    private static IList<double> ParseBoundaries(
        Dictionary<string, object> tableObject, string key)
    {
        if (!tableObject.TryGetValue(key, out object value)
            || !(value is IEnumerable values)
            || value is string)
            return new List<double>();

        return values.Cast<object>()
            .Select(Convert.ToDouble)
            .Where(position => !double.IsNaN(position)
                && !double.IsInfinity(position)
                && position > 0
                && position < 1)
            .Distinct()
            .OrderBy(position => position)
            .ToList();
    }

    private static bool ParseBoolean(Dictionary<string, object> obj, string key)
    {
        if (!obj.TryGetValue(key, out object value) || value == null)
            return false;

        if (value is bool boolValue)
            return boolValue;

        if (value is string stringValue && bool.TryParse(stringValue, out bool parsed))
            return parsed;

        return false;
    }

    /// <summary>
    /// Parses a <c>rotate-page</c> message into a <see cref="RotatePagePayload"/>.
    /// Returns <c>null</c> on failure.
    /// </summary>
    public static RotatePagePayload ParseRotatePage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var obj = WebMessageParser.Serializer.Deserialize<Dictionary<string, object>>(json);
            if (obj == null) return null;

            string pdfId     = obj.TryGetValue("pdfId",     out object pidVal) ? pidVal as string : null;
            int    page      = obj.TryGetValue("page",      out object pgVal)  ? Convert.ToInt32(pgVal) : 0;
            string direction = obj.TryGetValue("direction", out object dirVal) ? dirVal as string : null;

            if (string.IsNullOrWhiteSpace(pdfId) || string.IsNullOrWhiteSpace(direction))
                return null;

            return new RotatePagePayload { PdfId = pdfId, Page = page, Direction = direction };
        }
        catch
        {
            return null;
        }
    }
    }

    /// <summary>Deserialized payload for an <c>excel-navigate</c> message.</summary>
    internal sealed class ExcelNavigatePayload
    {
        public Services.ExcelCellNavigationService.Motion Motion { get; set; }

        public bool Reverse { get; set; }
    }

    internal sealed class LinkRectangleDeletedPayload
    {
        public string Id { get; set; }
        public bool DeleteCellData { get; set; }
    }

    internal sealed class CopyTableSelectionPayload
    {
        public string Id { get; set; }
        public IList<TableSelectionCopyTargetPayload> Targets { get; set; }
    }

    internal sealed class TableSelectionCopyTargetPayload
    {
        public int Page { get; set; }
        public TableGrid TableGrid { get; set; }
        public IList<IList<string>> TableCells { get; set; }
    }

    /// <summary>Deserialized payload for a <c>link-rectangle-created</c> message.</summary>
    internal sealed class LinkRectangleCreatedPayload
    {
        public string   PdfId    { get; set; }
        public int      Page     { get; set; }
        public double   X        { get; set; }
        public double   Y        { get; set; }
        public double   Width    { get; set; }
        public double   Height   { get; set; }
        public string   Text     { get; set; }
        public LinkType LinkType { get; set; }
        public bool     AppendToActiveSum { get; set; }
        public TableGrid TableGrid { get; set; }
        public IList<IList<string>> TableCells { get; set; }
    }

    /// <summary>Deserialized payload for a <c>rotate-page</c> message.</summary>
    internal sealed class RotatePagePayload
    {
        public string PdfId     { get; set; }
        public int    Page      { get; set; }
        public string Direction { get; set; }
    }

    /// <summary>Deserialized payload for a <c>link-rectangle-updated</c> message.</summary>
    internal sealed class LinkRectangleUpdatedPayload
    {
        public string Id     { get; set; }
        public string PdfId  { get; set; }
        public int    Page   { get; set; }
        public double X      { get; set; }
        public double Y      { get; set; }
        public double Width  { get; set; }
        public double Height { get; set; }
        public string Text   { get; set; }
        public TableGrid TableGrid { get; set; }
        public IList<IList<string>> TableCells { get; set; }
    }
}
