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
                return new DocuLinkStorage(DocuLinkXml.SchemaVersion, new PdfFolder[0], new PdfDocument[0], new LinkedRectangle[0]);

            string xml = part.XML;
            if (string.IsNullOrWhiteSpace(xml))
                return new DocuLinkStorage(DocuLinkXml.SchemaVersion, new PdfFolder[0], new PdfDocument[0], new LinkedRectangle[0]);

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

        // ── PDF operations ────────────────────────────────────────────────────

        public bool TryGetPdf(string id, out PdfDocument pdf)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(id));

            DocuLinkStorage storage = Load();
            pdf = storage.Pdfs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            return pdf != null;
        }

        public void UpsertPdf(PdfDocument pdf)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));
            if (string.IsNullOrWhiteSpace(pdf.Id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(pdf));

            DocuLinkStorage storage = Load();
            List<PdfDocument> pdfs = storage.Pdfs.ToList();
            int index = pdfs.FindIndex(p => string.Equals(p.Id, pdf.Id, StringComparison.Ordinal));
            if (index >= 0)
                pdfs[index] = pdf;
            else
                pdfs.Add(pdf);

            Save(new DocuLinkStorage(DocuLinkXml.SchemaVersion, storage.Folders, pdfs, storage.LinkedRectangles));
        }

        public bool RemovePdf(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("PDF id must be non-empty.", nameof(id));

            DocuLinkStorage storage = Load();
            List<PdfDocument> pdfs = storage.Pdfs.Where(p => !string.Equals(p.Id, id, StringComparison.Ordinal)).ToList();
            if (pdfs.Count == storage.Pdfs.Count)
                return false;

            Save(new DocuLinkStorage(DocuLinkXml.SchemaVersion, storage.Folders, pdfs, storage.LinkedRectangles));
            return true;
        }

        // ── Folder operations ─────────────────────────────────────────────────

        public void UpsertFolder(PdfFolder folder)
        {
            if (folder == null) throw new ArgumentNullException(nameof(folder));
            if (string.IsNullOrWhiteSpace(folder.Id))
                throw new ArgumentException("Folder id must be non-empty.", nameof(folder));

            DocuLinkStorage storage = Load();
            List<PdfFolder> folders = storage.Folders.ToList();
            int index = folders.FindIndex(f => string.Equals(f.Id, folder.Id, StringComparison.Ordinal));
            if (index >= 0)
                folders[index] = folder;
            else
                folders.Add(folder);

            Save(new DocuLinkStorage(DocuLinkXml.SchemaVersion, folders, storage.Pdfs, storage.LinkedRectangles));
        }

        public bool RemoveFolder(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Folder id must be non-empty.", nameof(id));

            DocuLinkStorage storage = Load();
            List<PdfFolder> folders = storage.Folders.Where(f => !string.Equals(f.Id, id, StringComparison.Ordinal)).ToList();
            if (folders.Count == storage.Folders.Count)
                return false;

            // Clear folderId on any PDFs that belonged to this folder.
            List<PdfDocument> pdfs = storage.Pdfs.Select(p =>
            {
                if (!string.Equals(p.FolderId, id, StringComparison.Ordinal))
                    return p;
                return new PdfDocument(p.Id, p.Name, p.Base64, null, p.DateAdded, p.FileSizeBytes)
                {
                    OcrStatus = p.OcrStatus,
                    GeometryBase64 = p.GeometryBase64,
                };
            }).ToList();

            Save(new DocuLinkStorage(DocuLinkXml.SchemaVersion, folders, pdfs, storage.LinkedRectangles));
            return true;
        }

        // ── LinkedRectangle operations ────────────────────────────────────────

        public bool TryGetLinkedRectangle(string id, out LinkedRectangle linkedRectangle)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("LinkedRectangle id must be non-empty.", nameof(id));

            DocuLinkStorage storage = Load();
            linkedRectangle = storage.LinkedRectangles.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            return linkedRectangle != null;
        }

        public void UpsertLinkedRectangle(LinkedRectangle linkedRectangle)
        {
            if (linkedRectangle == null) throw new ArgumentNullException(nameof(linkedRectangle));
            if (string.IsNullOrWhiteSpace(linkedRectangle.Id))
                throw new ArgumentException("LinkedRectangle id must be non-empty.", nameof(linkedRectangle));

            DocuLinkStorage storage = Load();
            List<LinkedRectangle> linkedRectangles = storage.LinkedRectangles.ToList();
            int index = linkedRectangles.FindIndex(r => string.Equals(r.Id, linkedRectangle.Id, StringComparison.Ordinal));
            if (index >= 0)
                linkedRectangles[index] = linkedRectangle;
            else
                linkedRectangles.Add(linkedRectangle);

            Save(new DocuLinkStorage(DocuLinkXml.SchemaVersion, storage.Folders, storage.Pdfs, linkedRectangles));
        }

        public bool RemoveLinkedRectangle(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("LinkedRectangle id must be non-empty.", nameof(id));

            DocuLinkStorage storage = Load();
            List<LinkedRectangle> linkedRectangles = storage.LinkedRectangles
                .Where(r => !string.Equals(r.Id, id, StringComparison.Ordinal))
                .ToList();
            if (linkedRectangles.Count == storage.LinkedRectangles.Count)
                return false;

            Save(new DocuLinkStorage(DocuLinkXml.SchemaVersion, storage.Folders, storage.Pdfs, linkedRectangles));
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
