using System.Xml.Linq;
using DocuLink.Addin.Modules.CustomXml.Models;

namespace DocuLink.Addin.Modules.CustomXml.Serialization
{
    internal static class DocuLinkPdfBinarySerializer
    {
        public static string ToXml(string pdfId, PdfBinaryParts parts)
        {
            XNamespace ns = XNamespace.Get(DocuLinkXml.PdfDataNamespaceUri(pdfId));

            var root = new XElement(ns + DocuLinkXml.PdfDataRootElementName,
                new XElement(ns + DocuLinkXml.Base64ElementName, parts?.Base64 ?? string.Empty));

            if (!string.IsNullOrEmpty(parts?.GeometryBase64))
                root.Add(new XElement(ns + DocuLinkXml.GeometryBase64ElementName, parts.GeometryBase64));

            if (!string.IsNullOrEmpty(parts?.TableStructureBase64))
                root.Add(new XElement(ns + DocuLinkXml.TableStructureBase64ElementName, parts.TableStructureBase64));

            if (!string.IsNullOrEmpty(parts?.FsValuesBase64))
                root.Add(new XElement(ns + DocuLinkXml.FsValuesBase64ElementName, parts.FsValuesBase64));

            return new XDocument(new XDeclaration("1.0", "utf-8", null), root)
                .ToString(SaveOptions.DisableFormatting);
        }

        public static PdfBinaryParts FromXml(string xml)
        {
            var parts = new PdfBinaryParts();

            if (string.IsNullOrWhiteSpace(xml))
                return parts;

            XDocument doc = XDocument.Parse(xml);
            XElement root = doc.Root;
            if (root == null)
                return parts;

            XNamespace ns = root.Name.Namespace;
            parts.Base64 = root.Element(ns + DocuLinkXml.Base64ElementName)?.Value ?? string.Empty;
            parts.GeometryBase64 = NullIfEmpty(root.Element(ns + DocuLinkXml.GeometryBase64ElementName)?.Value);
            parts.TableStructureBase64 = NullIfEmpty(root.Element(ns + DocuLinkXml.TableStructureBase64ElementName)?.Value);
            parts.FsValuesBase64 = NullIfEmpty(root.Element(ns + DocuLinkXml.FsValuesBase64ElementName)?.Value);
            return parts;
        }

        private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
