using System.Xml.Linq;

namespace DocuLink.Addin.Modules.CustomXml
{
    internal static class DocuLinkXml
    {
        public const string ContentNamespaceUri = "http://doculink.dev/schemas/storage/1/content";

        public static readonly XNamespace ContentNs = ContentNamespaceUri;

        public const string ContentRootElementName = "DocuLinkContent";

        public const string LinksNamespaceUri = "http://doculink.dev/schemas/storage/1/links";

        public static readonly XNamespace LinksNs = LinksNamespaceUri;

        public const string LinksRootElementName = "DocuLinkLinks";

        public const uint SchemaVersion = 1;

        public const string FoldersElementName = "Folders";

        public const string FolderElementName = "Folder";

        public const string PdfsElementName = "Pdfs";

        public const string PdfElementName = "Pdf";

        public const string LinkedRectanglesElementName = "LinkedRectangles";

        public const string LinkedRectangleElementName = "LinkedRectangle";

        public const string CellElementName = "Cell";

        public const string RectElementName = "Rect";

        public const string PdfIdAttribute = "pdfId";

        public const string FolderIdAttribute = "folderId";

        public const string DateAddedAttribute = "dateAdded";

        public const string FileSizeBytesAttribute = "fileSizeBytes";

        public const string OcrStatusAttribute = "ocrStatus";

        public const string GeometryBase64Attribute = "geometryBase64";

        // Per-PDF binary XML parts
        public const string PdfDataNamespaceBase = "http://doculink.dev/schemas/storage/1/pdf-data/";

        public static string PdfDataNamespaceUri(string pdfId) => PdfDataNamespaceBase + pdfId;

        public const string PdfDataRootElementName = "PdfData";

        public const string Base64ElementName = "Base64";

        public const string GeometryBase64ElementName = "GeometryBase64";

        // Page rotation storage
        public const string PageRotationsElementName = "PageRotations";

        public const string PageRotationElementName = "Page";

        public const string PageIndexAttribute = "index";

        public const string RotationAttribute = "rotation";
    }
}
