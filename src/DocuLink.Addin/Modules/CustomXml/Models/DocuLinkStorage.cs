using System.Collections.Generic;

namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class DocuLinkStorage
    {
        public DocuLinkStorage(uint version, IEnumerable<PdfDocument> pdfs, IEnumerable<LinkedRectangle> linkedRectangles)
        {
            Version = version;
            Pdfs = pdfs != null ? new List<PdfDocument>(pdfs) : new List<PdfDocument>();
            LinkedRectangles = linkedRectangles != null ? new List<LinkedRectangle>(linkedRectangles) : new List<LinkedRectangle>();
        }

        public uint Version { get; set; }

        public IList<PdfDocument> Pdfs { get; }

        public IList<LinkedRectangle> LinkedRectangles { get; }
    }
}
