using System;
using System.Linq;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Handles both directions of link navigation between Excel cells and PDF
    /// rectangles. Keeps navigation lookups out of the WebView host layer.
    /// </summary>
    internal sealed class LinkNavigationService
    {
        public bool NavigateToLinkedCell(string rectId, Excel.Workbook workbook)
        {
            if (string.IsNullOrWhiteSpace(rectId) || workbook == null)
                return false;

            var rect = Globals.ThisAddIn.GetStorageSession(workbook).GetLinks()
                .FirstOrDefault(r => r.Id == rectId);

            if (rect == null)
                return false;

            try
            {
                Excel.Worksheet ws = FindWorksheet(workbook, rect.LinkedCell.SheetName);
                if (ws == null)
                    return false;

                ws.Activate();
                ((Excel.Range)ws.Range[rect.LinkedCell.Address]).Select();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] LinkNavigationService.NavigateToLinkedCell failed: {ex.Message}");
                return false;
            }
        }

        public LinkedRectangle FindRectangleForCell(
            string sheetName,
            string address,
            Excel.Workbook workbook)
        {
            if (string.IsNullOrWhiteSpace(sheetName) ||
                string.IsNullOrWhiteSpace(address)   ||
                workbook == null)
                return null;

            try
            {
                return Globals.ThisAddIn.GetStorageSession(workbook).GetLinks().FirstOrDefault(r =>
                    string.Equals(r.LinkedCell.SheetName, sheetName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.LinkedCell.Address,   address,   StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] LinkNavigationService.FindRectangleForCell failed: {ex.Message}");
                return null;
            }
        }

        private static Excel.Worksheet FindWorksheet(Excel.Workbook workbook, string sheetName)
        {
            foreach (Excel.Worksheet ws in workbook.Worksheets)
            {
                if (string.Equals(ws.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                    return ws;
            }
            return null;
        }
    }
}
