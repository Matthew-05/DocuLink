using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>
    /// Parses inbound viewer→host messages that conform to
    /// contracts/webview-messages-v1.json.
    /// </summary>
    internal static class HostMessageParser
    {
        private static readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        /// <summary>
        /// Returns the value of the <c>type</c> property in the JSON object, or
        /// <c>null</c> if the string is not a valid JSON object or has no <c>type</c>.
        /// </summary>
        public static string GetMessageType(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var obj = _serializer.Deserialize<Dictionary<string, object>>(json);
                if (obj != null && obj.TryGetValue("type", out object typeValue))
                    return typeValue as string;
            }
            catch
            {
                // Malformed JSON — not a recognised message.
            }

            return null;
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
                var obj = _serializer.Deserialize<Dictionary<string, object>>(json);
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
