using System.Collections.Generic;

namespace Talliark.Addin.Modules.CustomXml.Models
{
    public sealed class TalliarkStorage
    {
        public TalliarkStorage(uint version,
            IEnumerable<PdfFolder> folders,
            IEnumerable<PdfMetadata> pdfs,
            IEnumerable<LinkedRectangle> linkedRectangles)
        {
            Version = version;
            Folders = folders != null ? new List<PdfFolder>(folders) : new List<PdfFolder>();
            Pdfs = pdfs != null ? new List<PdfMetadata>(pdfs) : new List<PdfMetadata>();
            LinkedRectangles = linkedRectangles != null ? new List<LinkedRectangle>(linkedRectangles) : new List<LinkedRectangle>();
        }

        public uint Version { get; set; }

        public IList<PdfFolder> Folders { get; }

        public IList<PdfMetadata> Pdfs { get; }

        public IList<LinkedRectangle> LinkedRectangles { get; }
    }
}
