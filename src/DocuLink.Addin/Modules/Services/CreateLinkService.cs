using System;
using System.Collections.Generic;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;
using static DocuLink.Addin.Modules.DocuLinkLog;

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

        /// <summary>
        /// Returns the new <see cref="LinkedRectangle"/> and the complete updated list of
        /// all linked rectangles so callers can propagate the data to the viewer without a
        /// second round-trip to storage.
        /// </summary>
        public (LinkedRectangle LinkedRect, IList<LinkedRectangle> AllRects) CreateLink(
            string pdfId,
            int page,
            double x, double y, double width, double height,
            string text,
            Excel.Workbook workbook)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            Excel.Application app = Globals.ThisAddIn.Application;
            var selection = app?.Selection as Excel.Range;
            if (selection == null) return (null, null);

            Excel.Range startCell = (Excel.Range)selection.Cells[1, 1];
            Excel.Range cell = startCell;

            Trace($"startCell={startCell.get_Address()}");
            for (int col = 0; col < MaxSearchColumns; col++)
            {
                object value = cell.Value2;
                if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                    break;

                cell = cell.get_Offset(0, 1);
            }
            Trace($"target cell={cell.get_Address()} (moved={cell.Column != startCell.Column})");

            Trace($"setting Value2='{text}'");
            cell.Value2 = text;
            Trace("Value2 set; applying style");
            CellFormatter.ApplyLinkStyle(cell);
            Trace("style applied");

            if (cell.Row != startCell.Row || cell.Column != startCell.Column)
            {
                Trace("cell moved right – calling Activate+Select");
                ((Excel.Worksheet)cell.Worksheet).Activate();
                cell.Select();
                Trace("Activate+Select done");
            }
            else
            {
                Trace("cell did NOT move – no Select called");
            }

            string sheetName = ((Excel.Worksheet)cell.Worksheet).Name;
            string address   = cell.get_Address(true, true);

            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);

            IList<LinkedRectangle> links;
            int trackIndex;
            using (Time("GetLinks + NextTrackIndex"))
            {
                links = session.GetLinks();
                trackIndex = LinkCellTracker.NextTrackIndex(links);
            }

            var linkedCell = new LinkedCell(sheetName, address, trackIndex);
            var rect       = new PdfRectangle(page, x, y, width, height, RectangleCoordinateSpace.Normalized);
            var linkedRect = new LinkedRectangle(Guid.NewGuid().ToString("D"), pdfId, linkedCell, rect);

            using (Time("SaveLinks"))
            {
                session.AddLink(linkedRect);
            }

            Trace("calling BindCell");
            LinkCellTracker.BindCell(workbook, cell, trackIndex);
            Trace("BindCell done – returning");

            return (linkedRect, session.GetLinks());
        }
    }
}
