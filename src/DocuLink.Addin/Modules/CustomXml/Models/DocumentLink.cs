namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class DocumentLink
    {
        public DocumentLink(string id, string pdfBase64, LinkedCellRef linkedCell, PdfRectangle rectangle)
        {
            Id = id;
            PdfBase64 = pdfBase64;
            LinkedCell = linkedCell;
            Rectangle = rectangle;
        }

        public string Id { get; set; }

        public string PdfBase64 { get; set; }

        public LinkedCellRef LinkedCell { get; set; }

        public PdfRectangle Rectangle { get; set; }
    }
}
