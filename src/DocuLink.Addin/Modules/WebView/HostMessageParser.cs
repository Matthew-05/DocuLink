using System;
using System.Collections.Generic;

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
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var obj = WebMessageParser.Serializer.Deserialize<Dictionary<string, object>>(json);
                if (obj == null) return null;

                string pdfId = obj.TryGetValue("pdfId", out object pidVal) ? pidVal as string : null;
                int    page  = obj.TryGetValue("page",  out object pgVal)  ? Convert.ToInt32(pgVal) : 0;
                string text  = obj.TryGetValue("text",  out object txtVal) ? (txtVal as string ?? "") : "";

                double rx = 0, ry = 0, rw = 0, rh = 0;
                if (obj.TryGetValue("rect", out object rectVal)
                    && rectVal is Dictionary<string, object> rect)
                {
                    rx = rect.TryGetValue("x",      out object xv) ? Convert.ToDouble(xv) : 0;
                    ry = rect.TryGetValue("y",      out object yv) ? Convert.ToDouble(yv) : 0;
                    rw = rect.TryGetValue("width",  out object wv) ? Convert.ToDouble(wv) : 0;
                    rh = rect.TryGetValue("height", out object hv) ? Convert.ToDouble(hv) : 0;
                }

                return new LinkRectangleCreatedPayload
                {
                    PdfId  = pdfId,
                    Page   = page,
                    X      = rx,
                    Y      = ry,
                    Width  = rw,
                    Height = rh,
                    Text   = text,
                };
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
        public string PdfId  { get; set; }
        public int    Page   { get; set; }
        public double X      { get; set; }
        public double Y      { get; set; }
        public double Width  { get; set; }
        public double Height { get; set; }
        public string Text   { get; set; }
    }
}
