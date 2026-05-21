using System.Xml.Linq;

namespace DocuLink.Addin.Modules.CustomXml
{
    internal static class DocuLinkXml
    {
        public const string NamespaceUri = "http://doculink.dev/schemas/storage/1";

        public static readonly XNamespace Ns = NamespaceUri;

        public const uint SchemaVersion = 1;

        public const string RootElementName = "DocuLinkStore";

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
    }
}
