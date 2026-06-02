using System;
using System.Collections.Generic;
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
        return LinkType.Auto;
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
    }
}
