using System;
using System.Collections.Generic;
using System.Linq;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;
using static DocuLink.Addin.Modules.DocuLinkLog;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Handles the full link-rectangle creation flow on the C# side:
    /// finds an empty target cell, inserts the extracted text (formatted
    /// according to the chosen <see cref="LinkType"/>), applies the link style,
    /// and persists the <see cref="LinkedRectangle"/> to storage.
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
            LinkType linkType,
            bool appendToActiveSum,
            Excel.Workbook workbook)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            Excel.Application app = Globals.ThisAddIn.Application;
            var selection = app?.Selection as Excel.Range;
            if (selection == null) return (null, null);

            Excel.Range startCell = (Excel.Range)selection.Cells[1, 1];

            Trace($"startCell={startCell.get_Address()}");

            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);

            // Sum-append: if the currently active cell is already a Sum link cell, add
            // this rectangle's numbers to its formula instead of targeting a new cell.
            if (linkType == LinkType.Sum && appendToActiveSum)
            {
                using (Time("SumAppendCheck"))
                {
                    var (appendRect, appendAll) = TryAppendToSumCell(
                        startCell, text, pdfId, page, x, y, width, height, session, workbook);
                    if (appendRect != null)
                        return (appendRect, appendAll);
                }
            }

            Excel.Range cell = startCell;
            for (int col = 0; col < MaxSearchColumns; col++)
            {
                object value = cell.Value2;
                if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                    break;

                cell = cell.get_Offset(0, 1);
            }
            Trace($"target cell={cell.get_Address()} (moved={cell.Column != startCell.Column})");

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

            IList<LinkedRectangle> links;
            int trackIndex;
            using (Time("GetLinks + NextTrackIndex"))
            {
                links = session.GetLinks();
                trackIndex = LinkCellTracker.NextTrackIndex(links);
            }

            var linkedCell = new LinkedCell(sheetName, address, trackIndex);
            var rect       = new PdfRectangle(page, x, y, width, height, RectangleCoordinateSpace.Normalized);
            var linkedRect = new LinkedRectangle(Guid.NewGuid().ToString("D"), pdfId, linkedCell, rect)
            {
                LinkType   = linkType,
                SourceText = linkType == LinkType.Sum ? text : null,
            };

            Trace("calling BindCell");
            LinkCellTracker.BindCell(workbook, cell, trackIndex);
            Trace("BindCell done");

            try
            {
                WriteToCell(cell, text, linkType);
                CellFormattingService.ApplyLinkStyle(cell, linkType);
                Trace("style applied");
            }
            catch
            {
                LinkCellTracker.UnbindCell(workbook, cell, trackIndex);
                throw;
            }

            using (Time("SaveLinks"))
            {
                session.AddLink(linkedRect);
            }
            Trace("returning");

            return (linkedRect, session.GetLinks());
        }

        /// <summary>
        /// Creates a link writing directly to <paramref name="targetCell"/>, bypassing the
        /// "search rightward for empty cell" logic used in the interactive flow.
        /// Used by the document-matcher batch workflow.
        /// </summary>
        public (LinkedRectangle LinkedRect, IList<LinkedRectangle> AllRects) CreateLinkAtCell(
            string pdfId,
            int page,
            double x, double y, double width, double height,
            string text,
            LinkType linkType,
            Excel.Range targetCell,
            Excel.Workbook workbook)
        {
            if (workbook == null)   throw new ArgumentNullException(nameof(workbook));
            if (targetCell == null) throw new ArgumentNullException(nameof(targetCell));
            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            Trace($"CreateLinkAtCell cell={targetCell.get_Address()}");

            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);

            string sheetName = ((Excel.Worksheet)targetCell.Worksheet).Name;
            string address   = targetCell.get_Address(true, true);

            IList<LinkedRectangle> links;
            int trackIndex;
            using (Time("GetLinks + NextTrackIndex"))
            {
                links = session.GetLinks();
                trackIndex = LinkCellTracker.NextTrackIndex(links);
            }

            var linkedCell = new LinkedCell(sheetName, address, trackIndex);
            var rect       = new PdfRectangle(page, x, y, width, height, RectangleCoordinateSpace.Normalized);
            var linkedRect = new LinkedRectangle(Guid.NewGuid().ToString("D"), pdfId, linkedCell, rect)
            {
                LinkType   = linkType,
                SourceText = linkType == LinkType.Sum ? text : null,
            };

            Trace("calling BindCell");
            LinkCellTracker.BindCell(workbook, targetCell, trackIndex);
            Trace("BindCell done");

            try
            {
                WriteToCell(targetCell, text, linkType);
                CellFormattingService.ApplyLinkStyle(targetCell, linkType);
                Trace("style applied");
            }
            catch
            {
                LinkCellTracker.UnbindCell(workbook, targetCell, trackIndex);
                throw;
            }

            using (Time("SaveLinks"))
            {
                session.AddLink(linkedRect);
            }
            Trace("returning");

            return (linkedRect, session.GetLinks());
        }

        /// <summary>
        /// If <paramref name="startCell"/> is already a DocuLink Sum link cell (verified via
        /// storage lookup), appends the new rectangle's numbers to the existing formula and
        /// adds a new <see cref="LinkedRectangle"/> pointing to the same cell.
        /// Returns <c>(null, null)</c> when the append condition is not met.
        /// </summary>
        private (LinkedRectangle, IList<LinkedRectangle>) TryAppendToSumCell(
            Excel.Range startCell,
            string text,
            string pdfId,
            int page,
            double x, double y, double width, double height,
            WorkbookStorageSession session,
            Excel.Workbook workbook)
        {
            string startAddress = startCell.get_Address(true, true);
            string startSheet   = ((Excel.Worksheet)startCell.Worksheet).Name;

            IList<LinkedRectangle> links = session.GetLinks();

            // Find sum rects whose resolved cell matches the active cell
            LinkedRectangle existingSum = null;
            foreach (var link in links)
            {
                if (link.LinkType != LinkType.Sum) continue;
                Excel.Range resolved = LinkCellResolver.TryResolveCell(workbook, link);
                if (resolved == null) continue;

                string resolvedSheet = ((Excel.Worksheet)resolved.Worksheet).Name;
                if (string.Equals(resolvedSheet, startSheet, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(resolved.get_Address(true, true), startAddress, StringComparison.Ordinal))
                {
                    existingSum = link;
                    break;
                }
            }

            if (existingSum == null) return (null, null);

            Trace($"Sum append: existing rect id={existingSum.Id}, cell={startAddress}");

            // Gather all sum rects for this cell (the ones we just found + the new one)
            var sumRectsForCell = links
                .Where(r => r.LinkType == LinkType.Sum && SameCell(r, existingSum))
                .Select(r => r.SourceText)
                .ToList();
            sumRectsForCell.Add(text);

            string formula = TextValueFormatter.RebuildSumFormula(sumRectsForCell);
            if (formula == null) formula = "0";

            try
            {
                CellFormattingService.ApplySumNumberFormat(startCell, sumRectsForCell);
                CellFormattingService.ApplyLinkStyle(startCell, LinkType.Sum);
                startCell.Formula = formula;
                startCell.Calculate();
            }
            catch (Exception ex) { Trace($"Sum append formula write failed: {ex.Message}"); }

            // New LinkedRectangle shares the same LinkedCell (same sheet/address/trackIndex)
            var rect       = new PdfRectangle(page, x, y, width, height, RectangleCoordinateSpace.Normalized);
            var linkedRect = new LinkedRectangle(Guid.NewGuid().ToString("D"), pdfId, existingSum.LinkedCell, rect)
            {
                LinkType   = LinkType.Sum,
                SourceText = text,
            };

            using (Time("SaveLinks (sum append)"))
            {
                session.AddLink(linkedRect);
            }

            return (linkedRect, session.GetLinks());
        }

        private static bool SameCell(LinkedRectangle a, LinkedRectangle b)
        {
            return a.LinkedCell.TrackIndex == b.LinkedCell.TrackIndex
                || (string.Equals(a.LinkedCell.SheetName, b.LinkedCell.SheetName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.LinkedCell.Address, b.LinkedCell.Address, StringComparison.Ordinal));
        }

        private static void WriteToCell(Excel.Range cell, string text, LinkType linkType)
        {
            Trace($"WriteToCell linkType={linkType} text='{text}'");
            switch (linkType)
            {
                case LinkType.Raw:
                    cell.Value2 = text;
                    break;

                case LinkType.Sum:
                    string formula = TextValueFormatter.BuildSumFormula(text);
                    if (formula != null)
                    {
                        CellFormattingService.ApplySumNumberFormat(cell, text);
                        cell.Formula = formula;
                        cell.Calculate();
                    }
                    else
                        cell.Value2 = text; // fallback: no numbers found, write raw
                    break;

                default: // Auto
                    cell.Value2 = TextValueFormatter.FormatAuto(text);
                    CellFormattingService.ApplyAutoNumberFormat(cell, text);
                    break;
            }
        }
    }
}
