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

        // ── Content (metadata) part ───────────────────────────────────────────

        public DocuLinkContent LoadContent()
        {
            Office.CustomXMLPart part = FindPartByNamespace(DocuLinkXml.ContentNamespaceUri);
            if (part == null)
                return new DocuLinkContent(DocuLinkXml.SchemaVersion, new PdfFolder[0], new PdfMetadata[0]);

            string xml = part.XML;
            if (string.IsNullOrWhiteSpace(xml))
                return new DocuLinkContent(DocuLinkXml.SchemaVersion, new PdfFolder[0], new PdfMetadata[0]);

            try
            {
                return DocuLinkContentSerializer.FromXDocument(XDocument.Parse(xml));
            }
            catch (System.Xml.XmlException ex)
            {
                throw new InvalidOperationException("DocuLink content custom XML part contains invalid XML.", ex);
            }
        }

        public void SaveContent(DocuLinkContent content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            string xml = DocuLinkContentSerializer.ToXDocument(content).ToString(SaveOptions.DisableFormatting);
            ReplacePart(DocuLinkXml.ContentNamespaceUri, xml);
        }

        // ── Per-PDF binary parts ──────────────────────────────────────────────

        public void SavePdfBinary(string id, string base64, string geometryBase64)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(id));

            string xml = DocuLinkPdfBinarySerializer.ToXml(id, base64, geometryBase64);
            ReplacePart(DocuLinkXml.PdfDataNamespaceUri(id), xml);
        }

        public bool TryLoadPdfBinary(string id, out string base64, out string geometryBase64)
        {
            base64 = string.Empty;
            geometryBase64 = null;

            if (string.IsNullOrWhiteSpace(id))
                return false;

            Office.CustomXMLPart part = FindPartByNamespace(DocuLinkXml.PdfDataNamespaceUri(id));
            if (part == null)
                return false;

            DocuLinkPdfBinarySerializer.FromXml(part.XML, out base64, out geometryBase64);
            return true;
        }

        public void DeletePdfBinary(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            DeletePart(DocuLinkXml.PdfDataNamespaceUri(id));
        }

        // ── Convenience: full PDF (metadata + binary) ─────────────────────────

        public bool TryGetPdf(string id, out PdfDocument pdf)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(id));

            DocuLinkContent content = LoadContent();
            PdfMetadata metadata = content.Pdfs.FirstOrDefault(
                p => string.Equals(p.Id, id, StringComparison.Ordinal));

            if (metadata == null)
            {
                pdf = null;
                return false;
            }

            TryLoadPdfBinary(id, out string base64, out string geometryBase64);
            pdf = new PdfDocument(metadata.Id, metadata.Name, base64 ?? string.Empty,
                metadata.FolderId, metadata.DateAdded, metadata.FileSizeBytes)
            {
                OcrStatus     = metadata.OcrStatus,
                GeometryBase64 = geometryBase64,
                PageRotations  = metadata.PageRotations,
            };
            return true;
        }

        public IList<PdfDocument> LoadAllPdfsWithBinary()
        {
            DocuLinkContent content = LoadContent();
            var result = new List<PdfDocument>(content.Pdfs.Count);
            foreach (PdfMetadata m in content.Pdfs)
            {
                TryLoadPdfBinary(m.Id, out string base64, out string geometryBase64);
                result.Add(new PdfDocument(m.Id, m.Name, base64 ?? string.Empty,
                    m.FolderId, m.DateAdded, m.FileSizeBytes)
                {
                    OcrStatus      = m.OcrStatus,
                    GeometryBase64 = geometryBase64,
                    PageRotations  = m.PageRotations,
                });
            }
            return result;
        }

        public void UpsertPdf(PdfDocument pdf)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));

            var metadata = new PdfMetadata(pdf.Id, pdf.Name, pdf.FolderId, pdf.DateAdded, pdf.FileSizeBytes)
            {
                OcrStatus     = pdf.OcrStatus,
                PageRotations = pdf.PageRotations,
            };
            UpsertMetadata(metadata);
            SavePdfBinary(pdf.Id, pdf.Base64, pdf.GeometryBase64);
        }

        // ── Metadata-only helpers ─────────────────────────────────────────────

        public bool TryGetMetadata(string id, out PdfMetadata metadata)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(id));

            DocuLinkContent content = LoadContent();
            metadata = content.Pdfs.FirstOrDefault(
                p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return metadata != null;
        }

        public void UpsertMetadata(PdfMetadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (string.IsNullOrWhiteSpace(metadata.Id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(metadata));

            DocuLinkContent content = LoadContent();
            List<PdfMetadata> pdfs = content.Pdfs.ToList();
            int index = pdfs.FindIndex(p => string.Equals(p.Id, metadata.Id, StringComparison.Ordinal));
            if (index >= 0) pdfs[index] = metadata;
            else pdfs.Add(metadata);

            SaveContent(new DocuLinkContent(content.Version, content.Folders, pdfs));
        }

        public bool RemovePdf(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(id));

            DocuLinkContent content = LoadContent();
            List<PdfMetadata> pdfs = content.Pdfs
                .Where(p => !string.Equals(p.Id, id, StringComparison.Ordinal))
                .ToList();

            if (pdfs.Count == content.Pdfs.Count)
                return false;

            SaveContent(new DocuLinkContent(content.Version, content.Folders, pdfs));
            DeletePdfBinary(id);
            return true;
        }

        // ── Folders ───────────────────────────────────────────────────────────

        public void UpsertFolder(PdfFolder folder)
        {
            if (folder == null) throw new ArgumentNullException(nameof(folder));
            if (string.IsNullOrWhiteSpace(folder.Id))
                throw new ArgumentException("Folder id must be non-empty.", nameof(folder));

            DocuLinkContent content = LoadContent();
            List<PdfFolder> folders = content.Folders.ToList();
            int index = folders.FindIndex(f => string.Equals(f.Id, folder.Id, StringComparison.Ordinal));
            if (index >= 0) folders[index] = folder;
            else folders.Add(folder);

            SaveContent(new DocuLinkContent(content.Version, folders, content.Pdfs));
        }

        public bool RemoveFolder(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Folder id must be non-empty.", nameof(id));

            DocuLinkContent content = LoadContent();
            List<PdfFolder> folders = content.Folders
                .Where(f => !string.Equals(f.Id, id, StringComparison.Ordinal))
                .ToList();

            if (folders.Count == content.Folders.Count)
                return false;

            // Move any PDFs in this folder to uncategorised
            List<PdfMetadata> pdfs = content.Pdfs.Select(p =>
            {
                if (!string.Equals(p.FolderId, id, StringComparison.Ordinal))
                    return p;
                return new PdfMetadata(p.Id, p.Name, null, p.DateAdded, p.FileSizeBytes)
                {
                    OcrStatus     = p.OcrStatus,
                    PageRotations = p.PageRotations,
                };
            }).ToList();

            SaveContent(new DocuLinkContent(content.Version, folders, pdfs));
            return true;
        }

        // ── Links part ────────────────────────────────────────────────────────

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
                return DocuLinkLinksSerializer.FromXDocument(XDocument.Parse(xml));
            }
            catch (System.Xml.XmlException ex)
            {
                throw new InvalidOperationException("DocuLink links custom XML part contains invalid XML.", ex);
            }
        }

        public void SaveLinks(IList<LinkedRectangle> linkedRectangles)
        {
            string xml = DocuLinkLinksSerializer.ToXDocument(linkedRectangles)
                .ToString(SaveOptions.DisableFormatting);
            ReplacePart(DocuLinkXml.LinksNamespaceUri, xml);
        }

        // ── Combined load/save (used by WorkbookStorageSession) ───────────────

        public DocuLinkStorage Load()
        {
            DocuLinkContent content = LoadContent();
            IList<LinkedRectangle> links = LoadLinks();
            return new DocuLinkStorage(content.Version, content.Folders, content.Pdfs, links);
        }

        public void Save(DocuLinkStorage storage)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            SaveContent(new DocuLinkContent(storage.Version, storage.Folders, storage.Pdfs));
            SaveLinks(storage.LinkedRectangles);
        }

        // ── LinkedRectangle helpers ───────────────────────────────────────────

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
            if (index >= 0) links[index] = linkedRectangle;
            else links.Add(linkedRectangle);

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

        // ── Store cleanup ─────────────────────────────────────────────────────

        public void DeleteStore()
        {
            // Delete all per-PDF binary parts first
            DocuLinkContent content = LoadContent();
            foreach (PdfMetadata pdf in content.Pdfs)
                DeletePdfBinary(pdf.Id);

            DeletePart(DocuLinkXml.ContentNamespaceUri);
            DeletePart(DocuLinkXml.LinksNamespaceUri);
        }

        // ── Private COM helpers ───────────────────────────────────────────────

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
