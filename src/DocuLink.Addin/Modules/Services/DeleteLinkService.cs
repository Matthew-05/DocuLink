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
        /// <summary>
        /// Deletes a single link rectangle by id. Clears cell background and
        /// unbinds the XmlMap. Cell text is preserved.
        /// </summary>
        /// <returns><c>true</c> if the rectangle was found and removed.</returns>
        public bool DeleteLink(string rectId, Excel.Workbook workbook)
        {
            if (string.IsNullOrWhiteSpace(rectId) || workbook == null)
                return false;

            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                return DeleteLinkCore(rectId, workbook);
            }
        }

        private bool DeleteLinkCore(string rectId, Excel.Workbook workbook)
        {
            try
            {
                var store = new DocuLinkCustomXmlPartStore(workbook);
                if (!store.TryGetLinkedRectangle(rectId, out LinkedRectangle rect))
                    return false;

                int trackIndex = rect.LinkedCell.TrackIndex;
                Excel.Range cell = TryResolveCell(workbook, rect);

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
                return store.RemoveLinkedRectangle(rectId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] DeleteLinkService.DeleteLink failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes all link rectangles whose linked cells fall within
        /// <paramref name="selection"/>. Supports multi-area selections.
        /// </summary>
        /// <returns>The ids of links successfully deleted.</returns>
        public IList<string> DeleteLinksInSelection(Excel.Range selection, Excel.Workbook workbook)
        {
            if (selection == null || workbook == null)
                return Array.Empty<string>();

            var store = new DocuLinkCustomXmlPartStore(workbook);
            DocuLinkStorage storage = store.Load();
            var idsToDelete = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                Excel.Areas areas = selection.Areas;
                int areaCount = areas?.Count ?? 1;

                if (areaCount > 1)
                {
                    foreach (Excel.Range area in areas)
                        CollectLinkedRectIds(area, storage, idsToDelete);
                }
                else
                {
                    CollectLinkedRectIds(selection, storage, idsToDelete);
                }
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] DeleteLinksInSelection iteration failed: {ex.Message}");
                CollectLinkedRectIds(selection, storage, idsToDelete);
            }

            if (idsToDelete.Count == 0)
                return Array.Empty<string>();

            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                var cellsToClear = new List<Excel.Range>();
                var unbindTargets = new List<(Excel.Range cell, int trackIndex)>();

                foreach (string id in idsToDelete)
                {
                    LinkedRectangle rect = storage.LinkedRectangles.FirstOrDefault(
                        r => string.Equals(r.Id, id, StringComparison.Ordinal));
                    if (rect == null)
                        continue;

                    Excel.Range cell = TryResolveCell(workbook, rect);
                    if (cell != null)
                        cellsToClear.Add(cell);

                    unbindTargets.Add((cell, rect.LinkedCell.TrackIndex));
                }

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
                            $"[DocuLink] DeleteLinksInSelection batch clear failed: {ex.Message}");
                    }

                    foreach ((Excel.Range cell, int trackIndex) in unbindTargets)
                    {
                        try { LinkCellTracker.UnbindCell(workbook, cell, trackIndex); }
                        catch (COMException ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[DocuLink] DeleteLinksInSelection UnbindCell failed: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    if (app != null)
                        app.EnableEvents = prevEnableEvents;
                }

                var remaining = storage.LinkedRectangles
                    .Where(r => !idsToDelete.Contains(r.Id))
                    .ToList();

                if (remaining.Count == storage.LinkedRectangles.Count)
                    return Array.Empty<string>();

                store.Save(new DocuLinkStorage(
                    DocuLinkXml.SchemaVersion,
                    storage.Folders,
                    storage.Pdfs,
                    remaining));

                return idsToDelete.ToList();
            }
        }

        private static void CollectLinkedRectIds(
            Excel.Range range,
            DocuLinkStorage storage,
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

                        LinkedRectangle rect = storage.LinkedRectangles.FirstOrDefault(
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

        private static Excel.Range TryResolveCell(Excel.Workbook workbook, LinkedRectangle rect)
        {
            try
            {
                Excel.Worksheet ws = FindWorksheet(workbook, rect.LinkedCell.SheetName);
                if (ws != null)
                {
                    Excel.Range cell = ws.Range[rect.LinkedCell.Address] as Excel.Range;
                    if (cell != null)
                        return cell;
                }
            }
            catch (COMException) { }

            return TryResolveCellViaXmlMap(workbook, rect.LinkedCell.TrackIndex);
        }

        private static Excel.Range TryResolveCellViaXmlMap(Excel.Workbook workbook, int trackIndex)
        {
            if (trackIndex <= 0)
                return null;

            string mapName = "DocuLink_" + trackIndex;
            Excel.XmlMap map = null;

            foreach (Excel.XmlMap candidate in workbook.XmlMaps)
            {
                try
                {
                    if (string.Equals(candidate.Name, mapName, StringComparison.Ordinal))
                    {
                        map = candidate;
                        break;
                    }
                }
                catch (COMException) { }
            }

            if (map == null)
                return null;

            const string linkXPath = "/DocuLinkLink";
            foreach (Excel.Worksheet ws in workbook.Worksheets)
            {
                try
                {
                    object result = ws.XmlDataQuery(linkXPath, Type.Missing, map);
                    if (result is Excel.Range range)
                        return range;
                }
                catch (COMException) { }
            }

            return null;
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
