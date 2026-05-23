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

        public DocuLinkContent LoadContent()
        {
            Office.CustomXMLPart part = FindPartByNamespace(DocuLinkXml.ContentNamespaceUri);
            if (part == null)
                return new DocuLinkContent(DocuLinkXml.SchemaVersion, new PdfFolder[0], new PdfDocument[0]);

            string xml = part.XML;
            if (string.IsNullOrWhiteSpace(xml))
                return new DocuLinkContent(DocuLinkXml.SchemaVersion, new PdfFolder[0], new PdfDocument[0]);

            try
            {
                XDocument document = XDocument.Parse(xml);
                return DocuLinkContentSerializer.FromXDocument(document);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new InvalidOperationException("DocuLink content custom XML part contains invalid XML.", ex);
            }
        }

        public void SaveContent(DocuLinkContent content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));

            XDocument document = DocuLinkContentSerializer.ToXDocument(content);
            string xml = document.ToString(SaveOptions.DisableFormatting);
            ReplacePart(DocuLinkXml.ContentNamespaceUri, xml);
        }

        public IList<LinkedRectangle> LoadLinks()
        {
            Office.CustomXMLPart part = FindPartByNamespace(DocuLinkXml.LinksNamespaceUri);
            if (part == null)
                return new List<LinkedRectangle>();

            string xml = part.XML;
            if (string.IsNullOrWhiteSpace(xml))
                return new List<LinkedRectangle>();

            try
            {
                XDocument document = XDocument.Parse(xml);
                return DocuLinkLinksSerializer.FromXDocument(document);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new InvalidOperationException("DocuLink links custom XML part contains invalid XML.", ex);
            }
        }

        public void SaveLinks(IList<LinkedRectangle> linkedRectangles)
        {
            XDocument document = DocuLinkLinksSerializer.ToXDocument(linkedRectangles);
            string xml = document.ToString(SaveOptions.DisableFormatting);
            ReplacePart(DocuLinkXml.LinksNamespaceUri, xml);
        }

        public DocuLinkStorage Load()
        {
            DocuLinkContent content = LoadContent();
            IList<LinkedRectangle> links = LoadLinks();
            return new DocuLinkStorage(
                content.Version,
                content.Folders,
                content.Pdfs,
                links);
        }

        public void Save(DocuLinkStorage storage)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            SaveContent(new DocuLinkContent(storage.Version, storage.Folders, storage.Pdfs));
            SaveLinks(storage.LinkedRectangles);
        }

        public bool TryGetPdf(string id, out PdfDocument pdf)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(id));

            DocuLinkContent content = LoadContent();
            pdf = content.Pdfs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return pdf != null;
        }

        public void UpsertPdf(PdfDocument pdf)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));
            if (string.IsNullOrWhiteSpace(pdf.Id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(pdf));

            DocuLinkContent content = LoadContent();
            List<PdfDocument> pdfs = content.Pdfs.ToList();
            int index = pdfs.FindIndex(p => string.Equals(p.Id, pdf.Id, StringComparison.Ordinal));
            if (index >= 0)
                pdfs[index] = pdf;
            else
                pdfs.Add(pdf);

            SaveContent(new DocuLinkContent(content.Version, content.Folders, pdfs));
        }

        public bool RemovePdf(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(id));

            DocuLinkContent content = LoadContent();
            List<PdfDocument> pdfs = content.Pdfs.Where(p => !string.Equals(p.Id, id, StringComparison.Ordinal)).ToList();
            if (pdfs.Count == content.Pdfs.Count)
                return false;

            SaveContent(new DocuLinkContent(content.Version, content.Folders, pdfs));
            return true;
        }

        public void UpsertFolder(PdfFolder folder)
        {
            if (folder == null) throw new ArgumentNullException(nameof(folder));
            if (string.IsNullOrWhiteSpace(folder.Id))
                throw new ArgumentException("Folder id must be non-empty.", nameof(folder));

            DocuLinkContent content = LoadContent();
            List<PdfFolder> folders = content.Folders.ToList();
            int index = folders.FindIndex(f => string.Equals(f.Id, folder.Id, StringComparison.Ordinal));
            if (index >= 0)
                folders[index] = folder;
            else
                folders.Add(folder);

            SaveContent(new DocuLinkContent(content.Version, folders, content.Pdfs));
        }

        public bool RemoveFolder(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Folder id must be non-empty.", nameof(id));

            DocuLinkContent content = LoadContent();
            List<PdfFolder> folders = content.Folders.Where(f => !string.Equals(f.Id, id, StringComparison.Ordinal)).ToList();
            if (folders.Count == content.Folders.Count)
                return false;

            List<PdfDocument> pdfs = content.Pdfs.Select(p =>
            {
                if (!string.Equals(p.FolderId, id, StringComparison.Ordinal))
                    return p;
                return new PdfDocument(p.Id, p.Name, p.Base64, null, p.DateAdded, p.FileSizeBytes)
                {
                    OcrStatus = p.OcrStatus,
                    GeometryBase64 = p.GeometryBase64,
                };
            }).ToList();

            SaveContent(new DocuLinkContent(content.Version, folders, pdfs));
            return true;
        }

        public bool TryGetLinkedRectangle(string id, out LinkedRectangle linkedRectangle)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("LinkedRectangle id must be non-empty.", nameof(id));

            IList<LinkedRectangle> links = LoadLinks();
            linkedRectangle = links.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            return linkedRectangle != null;
        }

        public void UpsertLinkedRectangle(LinkedRectangle linkedRectangle)
        {
            if (linkedRectangle == null) throw new ArgumentNullException(nameof(linkedRectangle));
            if (string.IsNullOrWhiteSpace(linkedRectangle.Id))
                throw new ArgumentException("LinkedRectangle id must be non-empty.", nameof(linkedRectangle));

            List<LinkedRectangle> links = LoadLinks().ToList();
            int index = links.FindIndex(r => string.Equals(r.Id, linkedRectangle.Id, StringComparison.Ordinal));
            if (index >= 0)
                links[index] = linkedRectangle;
            else
                links.Add(linkedRectangle);

            SaveLinks(links);
        }

        public bool RemoveLinkedRectangle(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("LinkedRectangle id must be non-empty.", nameof(id));

            List<LinkedRectangle> links = LoadLinks().ToList();
            int before = links.Count;
            links = links.Where(r => !string.Equals(r.Id, id, StringComparison.Ordinal)).ToList();
            if (links.Count == before)
                return false;

            SaveLinks(links);
            return true;
        }

        public void DeleteStore()
        {
            DeletePart(DocuLinkXml.ContentNamespaceUri);
            DeletePart(DocuLinkXml.LinksNamespaceUri);
        }

        private Office.CustomXMLPart FindPartByNamespace(string namespaceUri)
        {
            try
            {
                Office.CustomXMLParts matches = _workbook.CustomXMLParts.SelectByNamespace(namespaceUri);
                if (matches != null && matches.Count > 0)
                    return (Office.CustomXMLPart)matches[1];
            }
            catch (COMException) { }

            return null;
        }

        private void ReplacePart(string namespaceUri, string xml)
        {
            Office.CustomXMLPart existing = FindPartByNamespace(namespaceUri);
            if (existing != null)
                existing.Delete();

            object missing = Type.Missing;
            _workbook.CustomXMLParts.Add(xml, missing);
        }

        private void DeletePart(string namespaceUri)
        {
            Office.CustomXMLPart part = FindPartByNamespace(namespaceUri);
            if (part != null)
                part.Delete();
        }
    }
}
