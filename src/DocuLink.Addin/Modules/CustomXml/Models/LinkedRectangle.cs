namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class LinkedRectangle
    {
        public LinkedRectangle(string id, string pdfId, LinkedCell linkedCell, PdfRectangle rectangle)
        {
            Id = id;
            PdfId = pdfId;
            LinkedCell = linkedCell;
            Rectangle = rectangle;
        }

        public string Id { get; set; }

        public string PdfId { get; set; }

        public LinkedCell LinkedCell { get; set; }

        public PdfRectangle Rectangle { get; set; }
    }
}
