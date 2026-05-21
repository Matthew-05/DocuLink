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

        // ── PDF operations ────────────────────────────────────────────────────

        /// <summary>Adds a PDF supplied as a base64 string, optionally assigning it to a folder.</summary>
        public void AddPdf(Excel.Workbook workbook, string name, string base64, string folderId = null)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            _addService.AddEmbeddedPdfFromBase64(workbook, name, base64, folderId);
        }

        /// <summary>Embeds a PDF from disk (used for OS/WebView2 file drops where JS does not receive the drop).</summary>
        public void AddPdfFromFilePath(Excel.Workbook workbook, string pdfFilePath, string folderId = null)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(pdfFilePath))
                throw new ArgumentException("PDF path must be non-empty.", nameof(pdfFilePath));
            _addService.AddEmbeddedPdf(workbook, pdfFilePath, folderId);
        }

        /// <summary>Renames the PDF with the given id.</summary>
        public void RenamePdf(Excel.Workbook workbook, string id, string newName)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));
            if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName must be non-empty.", nameof(newName));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            DocuLinkStorage storage = store.Load();

            PdfDocument pdf = storage.Pdfs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (pdf == null)
                throw new InvalidOperationException("PDF not found: " + id);

            var updated = new PdfDocument(pdf.Id, newName.Trim(), pdf.Base64, pdf.FolderId, pdf.DateAdded, pdf.FileSizeBytes)
            {
                OcrStatus = pdf.OcrStatus
            };
            store.UpsertPdf(updated);
        }

        /// <summary>
        /// Removes the PDF with the given id and cascades to remove all linked rectangles that reference it.
        /// </summary>
        public void RemovePdf(Excel.Workbook workbook, string id)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            DocuLinkStorage storage = store.Load();

            List<PdfDocument> pdfs = storage.Pdfs
                .Where(p => !string.Equals(p.Id, id, StringComparison.Ordinal))
                .ToList();

            List<LinkedRectangle> rects = storage.LinkedRectangles
                .Where(r => !string.Equals(r.PdfId, id, StringComparison.Ordinal))
                .ToList();

            store.Save(new DocuLinkStorage(DocuLinkXml.SchemaVersion, storage.Folders, pdfs, rects));
        }

        /// <summary>Moves a PDF to a different folder. Pass null or empty to move to uncategorised.</summary>
        public void MoveFile(Excel.Workbook workbook, string id, string folderId)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            DocuLinkStorage storage = store.Load();

            PdfDocument pdf = storage.Pdfs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (pdf == null)
                throw new InvalidOperationException("PDF not found: " + id);

            string normalised = string.IsNullOrWhiteSpace(folderId) ? null : folderId.Trim();
            var updated = new PdfDocument(pdf.Id, pdf.Name, pdf.Base64, normalised, pdf.DateAdded, pdf.FileSizeBytes)
            {
                OcrStatus = pdf.OcrStatus
            };
            store.UpsertPdf(updated);
        }

        /// <summary>
        /// Replaces a PDF's bytes with the OCR-processed version and marks its status as "complete".
        /// Called by <see cref="OcrService"/> after a successful OCR run.
        /// </summary>
        public void UpdatePdfAfterOcr(Excel.Workbook workbook, string id, string newBase64)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));
            if (newBase64 == null) throw new ArgumentNullException(nameof(newBase64));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            DocuLinkStorage storage = store.Load();

            PdfDocument pdf = storage.Pdfs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (pdf == null)
                throw new InvalidOperationException("PDF not found: " + id);

            var updated = new PdfDocument(pdf.Id, pdf.Name, newBase64, pdf.FolderId, pdf.DateAdded, pdf.FileSizeBytes)
            {
                OcrStatus = "complete"
            };
            store.UpsertPdf(updated);
        }

        // ── Folder operations ─────────────────────────────────────────────────

        /// <summary>Creates a new folder with the given name and returns its generated id.</summary>
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

        /// <summary>Renames the folder with the given id.</summary>
        public void RenameFolder(Excel.Workbook workbook, string id, string newName)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));
            if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName must be non-empty.", nameof(newName));

            var store = new DocuLinkCustomXmlPartStore(workbook);
            DocuLinkStorage storage = store.Load();

            PdfFolder existing = storage.Folders.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
            if (existing == null)
                throw new InvalidOperationException("Folder not found: " + id);

            store.UpsertFolder(new PdfFolder(id, newName.Trim()));
        }

        /// <summary>
        /// Removes the folder with the given id. Any PDFs assigned to that folder become uncategorised.
        /// </summary>
        public void RemoveFolder(Excel.Workbook workbook, string id)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must be non-empty.", nameof(id));

            // RemoveFolder in the store already clears folderId on orphaned PDFs.
            var store = new DocuLinkCustomXmlPartStore(workbook);
            store.RemoveFolder(id);
        }
    }
}
