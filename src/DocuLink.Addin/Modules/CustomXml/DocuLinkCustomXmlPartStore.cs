using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using DocuLink.Addin.Modules.CustomXml.Models;
using DocuLink.Addin.Modules.CustomXml.Serialization;
using Excel = Microsoft.Office.Interop.Excel;
using Office = Microsoft.Office.Core;

namespace DocuLink.Addin.Modules.CustomXml
{
    public sealed class DocuLinkCustomXmlPartStore
    {
        private readonly Excel.Workbook _workbook;

        public DocuLinkCustomXmlPartStore(Excel.Workbook workbook)
        {
            _workbook = workbook ?? throw new ArgumentNullException(nameof(workbook));
        }

        public DocuLinkStorage Load()
        {
            Office.CustomXMLPart part = FindDocuLinkPart();
            if (part == null)
                return new DocuLinkStorage(DocuLinkXml.SchemaVersion, new DocumentLink[0]);

            string xml = part.XML;
            if (string.IsNullOrWhiteSpace(xml))
                return new DocuLinkStorage(DocuLinkXml.SchemaVersion, new DocumentLink[0]);

            try
            {
                XDocument document = XDocument.Parse(xml);
                return DocuLinkStorageSerializer.FromXDocument(document);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new InvalidOperationException("DocuLink custom XML part contains invalid XML.", ex);
            }
        }

        public void Save(DocuLinkStorage storage)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            XDocument document = DocuLinkStorageSerializer.ToXDocument(storage);
            string xml = document.ToString(SaveOptions.DisableFormatting);

            Office.CustomXMLPart existing = FindDocuLinkPart();
            if (existing != null)
                existing.Delete();

            Office.CustomXMLParts parts = _workbook.CustomXMLParts;
            object missing = Type.Missing;
            parts.Add(xml, missing);
        }

        public bool TryGetLink(string id, out DocumentLink link)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Link id must be non-empty.", nameof(id));

            DocuLinkStorage storage = Load();
            link = storage.Links.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal));
            return link != null;
        }

        public void UpsertLink(DocumentLink link)
        {
            if (link == null) throw new ArgumentNullException(nameof(link));
            if (string.IsNullOrWhiteSpace(link.Id))
                throw new ArgumentException("Document link id must be non-empty.", nameof(link));

            DocuLinkStorage storage = Load();
            List<DocumentLink> links = storage.Links.ToList();
            int index = links.FindIndex(l => string.Equals(l.Id, link.Id, StringComparison.Ordinal));
            if (index >= 0)
                links[index] = link;
            else
                links.Add(link);

            Save(new DocuLinkStorage(DocuLinkXml.SchemaVersion, links));
        }

        public bool RemoveLink(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Link id must be non-empty.", nameof(id));

            DocuLinkStorage storage = Load();
            List<DocumentLink> links = storage.Links.Where(l => !string.Equals(l.Id, id, StringComparison.Ordinal)).ToList();
            if (links.Count == storage.Links.Count)
                return false;

            Save(new DocuLinkStorage(DocuLinkXml.SchemaVersion, links));
            return true;
        }

        public void DeleteStore()
        {
            Office.CustomXMLPart part = FindDocuLinkPart();
            if (part != null)
                part.Delete();
        }

        private Office.CustomXMLPart FindDocuLinkPart()
        {
            Office.CustomXMLParts parts = _workbook.CustomXMLParts;
            foreach (Office.CustomXMLPart part in parts)
            {
                if (part == null)
                    continue;

                string xml = part.XML;
                if (string.IsNullOrWhiteSpace(xml))
                    continue;

                try
                {
                    XDocument document = XDocument.Parse(xml);
                    XElement root = document.Root;
                    if (root != null && root.Name == DocuLinkXml.Ns + DocuLinkXml.RootElementName)
                        return part;
                }
                catch (COMException)
                {
                    continue;
                }
                catch (System.Xml.XmlException)
                {
                    continue;
                }
            }

            return null;
        }
    }
}
