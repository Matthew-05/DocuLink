using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Removes persisted link rectangles and cleans up the associated Excel cell
    /// (background fill and XmlMap binding).
    /// </summary>
    internal sealed class DeleteLinkService
    {
        public bool DeleteLink(string rectId, Excel.Workbook workbook)
        {
            if (string.IsNullOrWhiteSpace(rectId) || workbook == null)
                return false;

            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                return DeleteLinkCore(rectId, workbook);
            }
        }

        private bool DeleteLinkCore(string rectId, Excel.Workbook workbook)
        {
            try
            {
                WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);
                if (!session.TryGetLink(rectId, out LinkedRectangle rect))
                    return false;

                int trackIndex = rect.LinkedCell.TrackIndex;
                Excel.Range cell = LinkCellResolver.TryResolveCell(workbook, rect);

                if (cell != null)
                {
                    try { CellFormatter.ClearLinkStyle(cell); }
                    catch (COMException ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DocuLink] DeleteLink ClearLinkStyle failed: {ex.Message}");
                    }
                }

                LinkCellTracker.UnbindCell(workbook, cell, trackIndex);
                return session.RemoveLink(rectId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] DeleteLinkService.DeleteLink failed: {ex.Message}");
                return false;
            }
        }

        public IList<string> DeleteLinksInSelection(Excel.Range selection, Excel.Workbook workbook)
        {
            if (selection == null || workbook == null)
                return Array.Empty<string>();

            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);
            IList<LinkedRectangle> links = session.GetLinks();
            var idsToDelete = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                Excel.Areas areas = selection.Areas;
                int areaCount = areas?.Count ?? 1;

                if (areaCount > 1)
                {
                    foreach (Excel.Range area in areas)
                        CollectLinkedRectIds(area, links, idsToDelete);
                }
                else
                {
                    CollectLinkedRectIds(selection, links, idsToDelete);
                }
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] DeleteLinksInSelection iteration failed: {ex.Message}");
                CollectLinkedRectIds(selection, links, idsToDelete);
            }

            if (idsToDelete.Count == 0)
                return Array.Empty<string>();

            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                return DeleteLinksByIdCore(
                    workbook,
                    session,
                    links,
                    idsToDelete,
                    "DeleteLinksInSelection");
            }
        }

        public IList<string> DeleteLinksForPdf(string pdfId, Excel.Workbook workbook)
        {
            if (string.IsNullOrWhiteSpace(pdfId) || workbook == null)
                return Array.Empty<string>();

            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);
            IList<LinkedRectangle> links = session.GetLinks();
            var idsToDelete = new HashSet<string>(
                links
                    .Where(r => string.Equals(r.PdfId, pdfId, StringComparison.Ordinal))
                    .Select(r => r.Id),
                StringComparer.Ordinal);

            if (idsToDelete.Count == 0)
                return Array.Empty<string>();

            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                return DeleteLinksByIdCore(
                    workbook,
                    session,
                    links,
                    idsToDelete,
                    "DeleteLinksForPdf");
            }
        }

        private static void CollectLinkedRectIds(
            Excel.Range range,
            IList<LinkedRectangle> links,
            HashSet<string> idsToDelete)
        {
            int rows = range.Rows.Count;
            int cols = range.Columns.Count;

            for (int r = 1; r <= rows; r++)
            {
                for (int c = 1; c <= cols; c++)
                {
                    Excel.Range cell = null;
                    try
                    {
                        cell = (Excel.Range)range.Cells[r, c];
                        int trackIndex = LinkCellTracker.FindTrackIndexForCell(cell);
                        if (trackIndex <= 0)
                            continue;

                        LinkedRectangle rect = links.FirstOrDefault(
                            lr => lr.LinkedCell.TrackIndex == trackIndex);
                        if (rect != null)
                            idsToDelete.Add(rect.Id);
                    }
                    catch (COMException ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DocuLink] CollectLinkedRectIds cell failed: {ex.Message}");
                    }
                }
            }
        }

        private static IList<string> DeleteLinksByIdCore(
            Excel.Workbook workbook,
            WorkbookStorageSession session,
            IList<LinkedRectangle> links,
            HashSet<string> idsToDelete,
            string operationName)
        {
            var cellsToClear = new List<Excel.Range>();
            var unbindTargets = new List<(Excel.Range cell, int trackIndex)>();
            var deletedIds = new List<string>();

            foreach (string id in idsToDelete)
            {
                LinkedRectangle rect = links.FirstOrDefault(
                    r => string.Equals(r.Id, id, StringComparison.Ordinal));
                if (rect == null)
                    continue;

                Excel.Range cell = LinkCellResolver.TryResolveCell(workbook, rect);
                if (cell != null)
                    cellsToClear.Add(cell);

                unbindTargets.Add((cell, rect.LinkedCell.TrackIndex));
                deletedIds.Add(id);
            }

            if (deletedIds.Count == 0)
                return Array.Empty<string>();

            Excel.Application app = Globals.ThisAddIn.Application;
            bool prevEnableEvents = app?.EnableEvents ?? true;

            try
            {
                if (app != null)
                    app.EnableEvents = false;

                try
                {
                    CellFormatter.ClearLinkStyles(cellsToClear, app);
                }
                catch (COMException ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DocuLink] {operationName} batch clear failed: {ex.Message}");
                }

                foreach ((Excel.Range cell, int trackIndex) in unbindTargets)
                {
                    try { LinkCellTracker.UnbindCell(workbook, cell, trackIndex); }
                    catch (COMException ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DocuLink] {operationName} UnbindCell failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                if (app != null)
                    app.EnableEvents = prevEnableEvents;
            }

            var remaining = links
                .Where(r => !idsToDelete.Contains(r.Id))
                .ToList();

            if (remaining.Count == links.Count)
                return Array.Empty<string>();

            session.SetLinks(remaining);
            return deletedIds;
        }
    }
}
