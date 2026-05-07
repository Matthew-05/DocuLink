using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>
    /// Shared JSON parsing helpers used by all WebView2 message parsers.
    /// </summary>
    internal static class WebMessageParser
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
    }
}
