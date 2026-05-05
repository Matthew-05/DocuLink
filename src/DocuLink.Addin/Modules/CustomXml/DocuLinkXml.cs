using System.Xml.Linq;

namespace DocuLink.Addin.Modules.CustomXml
{
    internal static class DocuLinkXml
    {
        public const string NamespaceUri = "http://doculink.dev/schemas/storage/1";

        public static readonly XNamespace Ns = NamespaceUri;

        public const uint SchemaVersion = 1;

        public const string RootElementName = "DocuLinkStore";

        public const string LinksElementName = "Links";

        public const string LinkElementName = "Link";

        public const string PdfElementName = "Pdf";

        public const string CellElementName = "Cell";

        public const string RectElementName = "Rect";
    }
}
