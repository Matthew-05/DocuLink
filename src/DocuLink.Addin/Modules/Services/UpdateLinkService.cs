using System;
using System.Runtime.InteropServices;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Updates an existing link rectangle's geometry and replaces the linked
    /// Excel cell text with re-extracted content from the viewer.
    /// </summary>
    internal sealed class UpdateLinkService
    {
        public bool UpdateLink(
            string rectId,
            int page,
            double x, double y, double width, double height,
            string text,
            Excel.Workbook workbook)
        {
            if (string.IsNullOrWhiteSpace(rectId) || workbook == null)
                return false;

            try
            {
                WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);
                if (!session.TryGetLink(rectId, out LinkedRectangle existing))
                    return false;

                Excel.Range cell = LinkCellResolver.TryResolveCell(workbook, existing);
                if (cell != null)
                {
                    try { cell.Value2 = text; }
                    catch (COMException ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DocuLink] UpdateLink cell write failed: {ex.Message}");
                    }
                }

                var rect = new PdfRectangle(page, x, y, width, height, RectangleCoordinateSpace.Normalized);
                var updated = new LinkedRectangle(existing.Id, existing.PdfId, existing.LinkedCell, rect);
                return session.UpdateLink(updated);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] UpdateLinkService.UpdateLink failed: {ex.Message}");
                return false;
            }
        }
    }
}
