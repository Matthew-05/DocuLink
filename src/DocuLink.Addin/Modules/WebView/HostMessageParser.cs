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
    }
}
