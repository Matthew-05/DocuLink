using System.Xml.Linq;

namespace DocuLink.Addin.Modules.CustomXml.Serialization
{
    internal static class DocuLinkPdfBinarySerializer
    {
        public static string ToXml(string pdfId, string base64, string geometryBase64)
        {
            XNamespace ns = XNamespace.Get(DocuLinkXml.PdfDataNamespaceUri(pdfId));

            var root = new XElement(ns + DocuLinkXml.PdfDataRootElementName,
                new XElement(ns + DocuLinkXml.Base64ElementName, base64 ?? string.Empty));

            if (!string.IsNullOrEmpty(geometryBase64))
                root.Add(new XElement(ns + DocuLinkXml.GeometryBase64ElementName, geometryBase64));

            return new XDocument(new XDeclaration("1.0", "utf-8", null), root)
                .ToString(SaveOptions.DisableFormatting);
        }

        public static void FromXml(string xml, out string base64, out string geometryBase64)
        {
            base64 = string.Empty;
            geometryBase64 = null;

            if (string.IsNullOrWhiteSpace(xml))
                return;

            XDocument doc = XDocument.Parse(xml);
            XElement root = doc.Root;
            if (root == null)
                return;

            XNamespace ns = root.Name.Namespace;
            base64 = root.Element(ns + DocuLinkXml.Base64ElementName)?.Value ?? string.Empty;
            string geo = root.Element(ns + DocuLinkXml.GeometryBase64ElementName)?.Value;
            geometryBase64 = string.IsNullOrEmpty(geo) ? null : geo;
        }
    }
}
