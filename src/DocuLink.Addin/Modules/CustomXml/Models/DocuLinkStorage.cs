using System.Collections.Generic;

namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class DocuLinkStorage
    {
        public DocuLinkStorage(uint version, IEnumerable<DocumentLink> links)
        {
            Version = version;
            Links = links != null ? new List<DocumentLink>(links) : new List<DocumentLink>();
        }

        public uint Version { get; set; }

        public IList<DocumentLink> Links { get; }
    }
}
