using System;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Handles the full link-rectangle creation flow on the C# side:
    /// finds an empty target cell, inserts the extracted text, applies the
    /// link style, and persists the <see cref="LinkedRectangle"/> to storage.
    /// </summary>
    internal sealed class CreateLinkService
    {
        private const int MaxSearchColumns = 100;

        public void CreateLink(
            string pdfId,
            int page,
            double x, double y, double width, double height,
            string text,
            Excel.Workbook workbook)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));

            Excel.Application app = Globals.ThisAddIn.Application;
            var selection = app?.Selection as Excel.Range;
            if (selection == null) return;

            // Start from the top-left cell of the selection and walk right
            // until an empty cell is found (max MaxSearchColumns to prevent runaway).
            Excel.Range cell = (Excel.Range)selection.Cells[1, 1];

            for (int col = 0; col < MaxSearchColumns; col++)
            {
                object value = cell.Value2;
                if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                    break;

                cell = cell.get_Offset(0, 1);
            }

            cell.Value2 = text;
            CellFormatter.ApplyLinkStyle(cell);

            string sheetName = ((Excel.Worksheet)cell.Worksheet).Name;
            string address   = cell.get_Address(true, true);

            var linkedCell   = new LinkedCell(sheetName, address);
            var rect         = new PdfRectangle(page, x, y, width, height, RectangleCoordinateSpace.Normalized);
            var linkedRect   = new LinkedRectangle(Guid.NewGuid().ToString("D"), pdfId, linkedCell, rect);

            new DocuLinkCustomXmlPartStore(workbook).UpsertLinkedRectangle(linkedRect);
        }
    }
}
