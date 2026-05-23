using System.Collections.Generic;

namespace DocuLink.Addin.Modules.CustomXml.Models
{
    /// <summary>
    /// Folders and embedded PDF catalogue persisted in the content Custom XML part.
    /// Linked rectangles live in a separate links part.
    /// </summary>
    public sealed class DocuLinkContent
    {
        public DocuLinkContent(uint version,
            IEnumerable<PdfFolder> folders,
            IEnumerable<PdfDocument> pdfs)
        {
            Version = version;
            Folders = folders != null ? new List<PdfFolder>(folders) : new List<PdfFolder>();
            Pdfs = pdfs != null ? new List<PdfDocument>(pdfs) : new List<PdfDocument>();
        }

        public uint Version { get; set; }

        public IList<PdfFolder> Folders { get; }

        public IList<PdfDocument> Pdfs { get; }
    }
}
