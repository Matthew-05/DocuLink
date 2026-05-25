using System.Collections.Generic;

namespace DocuLink.Addin.Modules.CustomXml.Models
{
    /// <summary>
    /// Folders and embedded PDF metadata catalogue persisted in the content Custom XML part.
    /// Binary data (base64 PDF bytes + geometry) lives in per-PDF binary Custom XML parts.
    /// Linked rectangles live in a separate links part.
    /// </summary>
    public sealed class DocuLinkContent
    {
        public DocuLinkContent(uint version,
            IEnumerable<PdfFolder> folders,
            IEnumerable<PdfMetadata> pdfs)
        {
            Version = version;
            Folders = folders != null ? new List<PdfFolder>(folders) : new List<PdfFolder>();
            Pdfs = pdfs != null ? new List<PdfMetadata>(pdfs) : new List<PdfMetadata>();
        }

        public uint Version { get; set; }

        public IList<PdfFolder> Folders { get; }

        public IList<PdfMetadata> Pdfs { get; }
    }
}
