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

                if (rect.LinkType == LinkType.Sum)
                {
                    IList<LinkedRectangle> allLinks = session.GetLinks();
                    var siblings = allLinks
                        .Where(r => !string.Equals(r.Id, rectId, StringComparison.Ordinal)
                                 && r.LinkedCell.TrackIndex == trackIndex)
                        .ToList();

                    if (siblings.Count > 0)
                        return DeleteSumRectPartial(rectId, rect, siblings, workbook, session);
                }

                Excel.Range cell = LinkCellResolver.TryResolveCell(workbook, rect);

                if (cell != null)
                {
                    try { CellFormattingService.ClearLinkStyle(cell); }
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

        private static bool DeleteSumRectPartial(
            string rectId,
            LinkedRectangle rect,
            IList<LinkedRectangle> siblings,
            Excel.Workbook workbook,
            WorkbookStorageSession session)
        {
            try
            {
                var remainingTexts = siblings.Select(r => r.SourceText).ToList();
                string formula = TextValueFormatter.RebuildSumFormula(remainingTexts);

                Excel.Range cell = LinkCellResolver.TryResolveCell(workbook, rect);
                if (cell != null)
                {
                    try
                    {
                        if (formula != null)
                        {
                            CellFormattingService.ApplySumNumberFormat(cell, remainingTexts);
                            cell.Formula = formula;
                            cell.Calculate();
                        }
                        else
                        {
                            cell.Value2 = null;
                        }
                    }
                    catch (COMException ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DocuLink] DeleteSumRectPartial cell update failed: {ex.Message}");
                    }
                }

                return session.RemoveLink(rectId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] DeleteSumRectPartial failed: {ex.Message}");
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
            var orphanBindings = new List<(Excel.Range cell, int trackIndex)>();
            var orphanTrackIndexes = new HashSet<int>();

            try
            {
                Excel.Areas areas = selection.Areas;
                int areaCount = areas?.Count ?? 1;

                if (areaCount > 1)
                {
                    foreach (Excel.Range area in areas)
                        CollectLinkedRectIds(workbook, area, links, idsToDelete, orphanBindings, orphanTrackIndexes);
                }
                else
                {
                    CollectLinkedRectIds(workbook, selection, links, idsToDelete, orphanBindings, orphanTrackIndexes);
                }
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] DeleteLinksInSelection iteration failed: {ex.Message}");
                CollectLinkedRectIds(workbook, selection, links, idsToDelete, orphanBindings, orphanTrackIndexes);
            }

            if (idsToDelete.Count == 0 && orphanBindings.Count == 0)
                return Array.Empty<string>();

            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                return DeleteLinksByIdCore(
                    workbook,
                    session,
                    links,
                    idsToDelete,
                    orphanBindings,
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
                    new List<(Excel.Range cell, int trackIndex)>(),
                    "DeleteLinksForPdf");
            }
        }

        private static void CollectLinkedRectIds(
            Excel.Workbook workbook,
            Excel.Range range,
            IList<LinkedRectangle> links,
            HashSet<string> idsToDelete,
            List<(Excel.Range cell, int trackIndex)> orphanBindings,
            HashSet<int> orphanTrackIndexes)
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

                        string cellSheet = ((Excel.Worksheet)cell.Worksheet).Name;
                        string cellAddress = cell.Address;

                        int trackIndex = LinkCellTracker.FindTrackIndexForCell(cell);
                        bool foundStoredLink = false;

                        if (trackIndex > 0)
                        {
                            foreach (LinkedRectangle lr in links)
                            {
                                if (lr.LinkedCell.TrackIndex == trackIndex)
                                {
                                    idsToDelete.Add(lr.Id);
                                    foundStoredLink = true;
                                }
                            }

                            if (!foundStoredLink && orphanTrackIndexes.Add(trackIndex))
                                orphanBindings.Add((cell, trackIndex));
                        }

                        foreach (LinkedRectangle lr in links)
                        {
                            if (!SameCellAddress(lr, cellSheet, cellAddress))
                                continue;

                            Excel.Range liveCell = LinkCellResolver.TryResolveCellViaXmlMap(
                                workbook,
                                lr.LinkedCell.TrackIndex);

                            if (liveCell != null && !SameCellAddress(liveCell, cellSheet, cellAddress))
                                continue;

                            idsToDelete.Add(lr.Id);
                        }
                    }
                    catch (COMException ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DocuLink] CollectLinkedRectIds cell failed: {ex.Message}");
                    }
                }
            }
        }

        private static bool SameCellAddress(LinkedRectangle link, string sheetName, string address)
        {
            if (link?.LinkedCell == null) return false;

            return string.Equals(link.LinkedCell.SheetName, sheetName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(link.LinkedCell.Address, address, StringComparison.Ordinal);
        }

        private static bool SameCellAddress(Excel.Range cell, string sheetName, string address)
        {
            if (cell == null) return false;

            try
            {
                string resolvedSheet = ((Excel.Worksheet)cell.Worksheet).Name;
                string resolvedAddress = cell.Address;

                return string.Equals(resolvedSheet, sheetName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(resolvedAddress, address, StringComparison.Ordinal);
            }
            catch (COMException)
            {
                return false;
            }
        }

        private static IList<string> DeleteLinksByIdCore(
            Excel.Workbook workbook,
            WorkbookStorageSession session,
            IList<LinkedRectangle> links,
            HashSet<string> idsToDelete,
            IList<(Excel.Range cell, int trackIndex)> orphanBindings,
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

            if (deletedIds.Count == 0 && (orphanBindings == null || orphanBindings.Count == 0))
                return Array.Empty<string>();

            if (orphanBindings != null)
            {
                foreach ((Excel.Range cell, int _) in orphanBindings)
                    if (cell != null)
                        cellsToClear.Add(cell);
            }

            Excel.Application app = Globals.ThisAddIn.Application;
            bool prevEnableEvents = app?.EnableEvents ?? true;

            try
            {
                if (app != null)
                    app.EnableEvents = false;

                try
                {
                    CellFormattingService.ClearLinkStyles(cellsToClear, app);
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

                if (orphanBindings != null)
                {
                    foreach ((Excel.Range cell, int trackIndex) in orphanBindings)
                    {
                        try { LinkCellTracker.UnbindCell(workbook, cell, trackIndex); }
                        catch (COMException ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[DocuLink] {operationName} orphan UnbindCell failed: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                if (app != null)
                    app.EnableEvents = prevEnableEvents;
            }

            if (deletedIds.Count == 0)
                return Array.Empty<string>();

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
