using System;
using System.Collections.Generic;
using System.Linq;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Provides file-management operations (rename, remove, move, folder CRUD) for the
    /// file-manager UI. All methods load and save via <see cref="DocuLinkCustomXmlPartStore"/>.
    /// </summary>
    public sealed class ManageFilesService
    {
        private readonly AddPdfDocumentService _addService = new AddPdfDocumentService();

        public void AddPdf(Excel.Workbook workbook, string name, string base64, string folderId = null)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            _addService.AddEmbeddedPdfFromBase64(workbook, name, base64, folderId);
        }

        public void AddPdfFromFilePath(Excel.Workbook workbook, string pdfFilePath, string folderId = null)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(pdfFilePath))
                throw new ArgumentException("PDF path must be non-empty.", nameof(pdfFilePath));
            _addService.AddEmbeddedPdf(workbook, pdfFilePath, folderId);
        }

        public void RenamePdf(Excel.Workbook workbook, string id, string newName)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));
            if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName must be non-empty.", nameof(newName));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            if (!store.TryGetPdf(id, out PdfDocument pdf))
                throw new InvalidOperationException("PDF not found: " + id);

            var updated = new PdfDocument(pdf.Id, newName.Trim(), pdf.Base64, pdf.FolderId, pdf.DateAdded, pdf.FileSizeBytes)
            {
                OcrStatus = pdf.OcrStatus,
                GeometryBase64 = pdf.GeometryBase64,
            };
            store.UpsertPdf(updated);
        }

        public void RemovePdf(Excel.Workbook workbook, string id)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            DocuLinkContent content = store.LoadContent();
            List<PdfDocument> pdfs = content.Pdfs
                .Where(p => !string.Equals(p.Id, id, StringComparison.Ordinal))
                .ToList();

            if (pdfs.Count == content.Pdfs.Count)
                throw new InvalidOperationException("PDF not found: " + id);

            store.SaveContent(new DocuLinkContent(content.Version, content.Folders, pdfs));

            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);
            var remainingLinks = session.GetLinks()
                .Where(r => !string.Equals(r.PdfId, id, StringComparison.Ordinal))
                .ToList();
            session.SetLinks(remainingLinks);
        }

        public void MoveFile(Excel.Workbook workbook, string id, string folderId)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            if (!store.TryGetPdf(id, out PdfDocument pdf))
                throw new InvalidOperationException("PDF not found: " + id);

            string normalised = string.IsNullOrWhiteSpace(folderId) ? null : folderId.Trim();
            var updated = new PdfDocument(pdf.Id, pdf.Name, pdf.Base64, normalised, pdf.DateAdded, pdf.FileSizeBytes)
            {
                OcrStatus = pdf.OcrStatus,
                GeometryBase64 = pdf.GeometryBase64,
            };
            store.UpsertPdf(updated);
        }

        public void UpdatePdfAfterOcr(Excel.Workbook workbook, string id, string newBase64, string geometryBase64)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));
            if (newBase64 == null) throw new ArgumentNullException(nameof(newBase64));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            if (!store.TryGetPdf(id, out PdfDocument pdf))
                throw new InvalidOperationException("PDF not found: " + id);

            var updated = new PdfDocument(pdf.Id, pdf.Name, newBase64, pdf.FolderId, pdf.DateAdded, pdf.FileSizeBytes)
            {
                OcrStatus = PdfStatus.Ocr,
                GeometryBase64 = geometryBase64,
            };
            store.UpsertPdf(updated);
        }

        public void UpdatePdfGeometry(Excel.Workbook workbook, string id, string geometryBase64)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            if (!store.TryGetPdf(id, out PdfDocument pdf))
                throw new InvalidOperationException("PDF not found: " + id);

            var updated = new PdfDocument(pdf.Id, pdf.Name, pdf.Base64, pdf.FolderId, pdf.DateAdded, pdf.FileSizeBytes)
            {
                OcrStatus = PdfStatus.Ocr,
                GeometryBase64 = geometryBase64,
            };
            store.UpsertPdf(updated);
        }

        public string AddFolder(Excel.Workbook workbook, string name)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must be non-empty.", nameof(name));

            string id = Guid.NewGuid().ToString("D");
            var folder = new PdfFolder(id, name.Trim());

            var store = new DocuLinkCustomXmlPartStore(workbook);
            store.UpsertFolder(folder);
            return id;
        }

        public void RenameFolder(Excel.Workbook workbook, string id, string newName)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));
            if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName must be non-empty.", nameof(newName));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            DocuLinkContent content = store.LoadContent();

            PdfFolder existing = content.Folders.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
            if (existing == null)
                throw new InvalidOperationException("Folder not found: " + id);

            store.UpsertFolder(new PdfFolder(id, newName.Trim()));
        }

        public void RemoveFolder(Excel.Workbook workbook, string id)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            store.RemoveFolder(id);
        }
    }
}
