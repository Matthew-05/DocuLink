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

        /// <summary>How extracted text is processed before writing to the Excel cell.</summary>
        public LinkType LinkType { get; set; } = LinkType.Auto;

        /// <summary>
        /// The raw extracted text captured at creation/update time.
        /// Used by Sum links to reconstruct the full formula when one of multiple
        /// contributing rectangles is resized.
        /// </summary>
        public string SourceText { get; set; }
    }
}
