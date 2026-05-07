using System;
using System.IO;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    public sealed class AddPdfDocumentService
    {
        /// <summary>Embeds PDF bytes in the workbook custom XML store without creating a linked rectangle.</summary>
        public void AddEmbeddedPdf(Excel.Workbook workbook, string pdfFilePath, string folderId = null)
        {
            if (workbook == null)
                throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(pdfFilePath))
                throw new ArgumentException("PDF path must be non-empty.", nameof(pdfFilePath));

            byte[] bytes = File.ReadAllBytes(pdfFilePath);
            string base64 = Convert.ToBase64String(bytes);

            string pdfId = Guid.NewGuid().ToString("D");
            string name = Path.GetFileName(pdfFilePath);
            var pdf = new PdfDocument(pdfId, name, base64, folderId, DateTime.UtcNow, bytes.LongLength);

            var store = new DocuLinkCustomXmlPartStore(workbook);
            store.UpsertPdf(pdf);
        }

        /// <summary>Embeds PDF bytes provided as a base64 string without reading from disk.</summary>
        public void AddEmbeddedPdfFromBase64(Excel.Workbook workbook, string name, string base64, string folderId = null)
        {
            if (workbook == null)
                throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("PDF name must be non-empty.", nameof(name));
            if (base64 == null)
                throw new ArgumentNullException(nameof(base64));

            long fileSizeBytes = 0;
            try { fileSizeBytes = Convert.FromBase64String(base64).LongLength; } catch { }

            string pdfId = Guid.NewGuid().ToString("D");
            var pdf = new PdfDocument(pdfId, name, base64, folderId, DateTime.UtcNow, fileSizeBytes);

            var store = new DocuLinkCustomXmlPartStore(workbook);
            store.UpsertPdf(pdf);
        }

        public void AddFromPdfFile(Excel.Workbook workbook, string pdfFilePath, LinkedCell linkedCell, string folderId = null)
        {
            if (workbook == null)
                throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(pdfFilePath))
                throw new ArgumentException("PDF path must be non-empty.", nameof(pdfFilePath));
            if (linkedCell == null)
                throw new ArgumentNullException(nameof(linkedCell));

            byte[] bytes = File.ReadAllBytes(pdfFilePath);
            string base64 = Convert.ToBase64String(bytes);

            string pdfId = Guid.NewGuid().ToString("D");
            string name = Path.GetFileName(pdfFilePath);
            var pdf = new PdfDocument(pdfId, name, base64, folderId, DateTime.UtcNow, bytes.LongLength);

            string rectId = Guid.NewGuid().ToString("D");
            var rectangle = new PdfRectangle(0, 0, 0, 1, 1, RectangleCoordinateSpace.Normalized);
            var linkedRectangle = new LinkedRectangle(rectId, pdfId, linkedCell, rectangle);

            var store = new DocuLinkCustomXmlPartStore(workbook);
            store.UpsertPdf(pdf);
            store.UpsertLinkedRectangle(linkedRectangle);
        }
    }
}
