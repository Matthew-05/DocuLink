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
        public void AddEmbeddedPdf(Excel.Workbook workbook, string pdfFilePath)
        {
            if (workbook == null)
                throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(pdfFilePath))
                throw new ArgumentException("PDF path must be non-empty.", nameof(pdfFilePath));

            byte[] bytes = File.ReadAllBytes(pdfFilePath);
            string base64 = Convert.ToBase64String(bytes);

            string pdfId = Guid.NewGuid().ToString("D");
            string name = Path.GetFileName(pdfFilePath);
            var pdf = new PdfDocument(pdfId, name, base64);

            var store = new DocuLinkCustomXmlPartStore(workbook);
            store.UpsertPdf(pdf);
        }

        public void AddFromPdfFile(Excel.Workbook workbook, string pdfFilePath, LinkedCell linkedCell)
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
            var pdf = new PdfDocument(pdfId, name, base64);

            string rectId = Guid.NewGuid().ToString("D");
            var rectangle = new PdfRectangle(0, 0, 0, 1, 1, RectangleCoordinateSpace.Normalized);
            var linkedRectangle = new LinkedRectangle(rectId, pdfId, linkedCell, rectangle);

            var store = new DocuLinkCustomXmlPartStore(workbook);
            store.UpsertPdf(pdf);
            store.UpsertLinkedRectangle(linkedRectangle);
        }
    }
}
