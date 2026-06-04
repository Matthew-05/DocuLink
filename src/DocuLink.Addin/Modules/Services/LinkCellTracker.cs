using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DocuLink.Addin.Modules;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Manages Excel XmlMap-based cell position tracking. Each linked cell is
    /// bound to a dedicated XmlMap named "DocuLink_{trackIndex}" via a trivial
    /// single-element schema. Excel's internal range-reference machinery keeps
    /// the binding accurate through cut/paste moves and sheet renames.
    /// </summary>
    internal static class LinkCellTracker
    {
        private const string MapNamePrefix = "DocuLink_";

        // Minimal schema — one string element per map; each map is unique to one link.
        private const string MapSchemaXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
            "<xs:element name=\"DocuLinkLink\" type=\"xs:string\"/>" +
            "</xs:schema>";

        private const string LinkXPath = "/DocuLinkLink";

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the next available TrackIndex for a new link by inspecting the
        /// largest existing TrackIndex and incrementing it.
        /// </summary>
        public static int NextTrackIndex(IEnumerable<LinkedRectangle> linkedRectangles)
        {
            if (linkedRectangles == null) throw new ArgumentNullException(nameof(linkedRectangles));
            int max = 0;
            foreach (LinkedRectangle r in linkedRectangles)
                if (r.LinkedCell.TrackIndex > max)
                    max = r.LinkedCell.TrackIndex;
            return max + 1;
        }

        /// <summary>
        /// Binds <paramref name="cell"/> to the XmlMap for <paramref name="trackIndex"/>,
        /// creating the map if it does not yet exist.
        /// </summary>
        public static void BindCell(Excel.Workbook workbook, Excel.Range cell, int trackIndex)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (cell == null) throw new ArgumentNullException(nameof(cell));
            if (trackIndex <= 0) throw new ArgumentOutOfRangeException(nameof(trackIndex));

            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            Excel.XmlMap map = EnsureMap(workbook, trackIndex, out bool created);
            ConfigureMapFormatting(map);

            // If the map was orphaned (e.g. prior delete that left the XmlMap behind),
            // its XPath is still bound to the old cell. Excel rejects a second SetValue
            // on the same map+XPath, so clear the stale binding first.
            if (!created)
            {
                Excel.Range stale = FindRangeForMap(workbook, map);
                if (stale != null)
                {
                    try { stale.XPath.Clear(); }
                    catch (COMException) { }
                }
            }

            Excel.Application app = null;
            bool restoreDisplayAlerts = false;
            bool previousDisplayAlerts = true;

            try
            {
                app = workbook.Application as Excel.Application;
                if (app != null)
                {
                    previousDisplayAlerts = app.DisplayAlerts;
                    app.DisplayAlerts = false;
                    restoreDisplayAlerts = true;
                }

                cell.XPath.SetValue(map, LinkXPath, Type.Missing, false);
            }
            finally
            {
                if (restoreDisplayAlerts && app != null)
                {
                    try { app.DisplayAlerts = previousDisplayAlerts; }
                    catch (COMException) { }
                }
            }
        }

        /// <summary>
        /// Removes the XPath binding from <paramref name="cell"/> and deletes the
        /// XmlMap for <paramref name="trackIndex"/> from the workbook.
        /// </summary>
        public static void UnbindCell(Excel.Workbook workbook, Excel.Range cell, int trackIndex)
        {
            if (workbook != null)
                WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            if (cell != null)
            {
                try { cell.XPath.Clear(); }
                catch (COMException) { }
            }

            if (workbook == null) return;

            Excel.XmlMap map = FindMap(workbook, trackIndex);
            try { map?.Delete(); }
            catch (COMException) { }
        }

        /// <summary>
        /// Returns the TrackIndex encoded in the XmlMap bound to <paramref name="cell"/>,
        /// or 0 if the cell carries no DocuLink XPath binding.
        /// </summary>
        public static int FindTrackIndexForCell(Excel.Range cell)
        {
            if (cell == null) return 0;

            try
            {
                var worksheet = cell.Worksheet as Excel.Worksheet;
                if (worksheet == null) return 0;
                var workbook = worksheet.Parent as Excel.Workbook;
                if (workbook == null) return 0;

                int cellRow = cell.Row;
                int cellCol = cell.Column;

                // Reverse lookup: probe each DocuLink XmlMap via XmlDataQuery rather than
                // calling cell.XPath.Map, which throws a COMException on unbound cells in
                // some Excel versions.
                foreach (Excel.XmlMap map in workbook.XmlMaps)
                {
                    string name;
                    try { name = map.Name; } catch (COMException) { continue; }

                    if (string.IsNullOrEmpty(name) ||
                        !name.StartsWith(MapNamePrefix, StringComparison.Ordinal))
                        continue;

                    string suffix = name.Substring(MapNamePrefix.Length);
                    if (!int.TryParse(suffix, out int idx) || idx <= 0) continue;

                    try
                    {
                        object result = worksheet.XmlDataQuery(LinkXPath, Type.Missing, map);
                        if (result is Excel.Range boundRange &&
                            boundRange.Row == cellRow &&
                            boundRange.Column == cellCol)
                            return idx;
                    }
                    catch (COMException) { }
                }

                return 0;
            }
            catch (COMException) { return 0; }
        }

        /// <summary>
        /// Queries each worksheet for the current range of every linked rectangle's
        /// XmlMap binding and updates the stored <see cref="LinkedCell.SheetName"/>
        /// and <see cref="LinkedCell.Address"/> in the workbook's Custom XML part.
        /// Call this on <c>WorkbookBeforeSave</c> to keep persisted addresses current.
        /// </summary>
        public static void SyncAllPositions(Excel.Workbook workbook)
        {
            if (workbook == null)
            {
                DocuLinkLog.Trace("ENTER workbook=(null) - return");
                return;
            }

            DocuLinkLog.Trace($"ENTER workbook={GetWorkbookDebugName(workbook)}");

            using (DocuLinkLog.Time("SyncAllPositions total"))
            {
            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            DocuLinkLog.Trace("getting storage session");
            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);

            DocuLinkLog.Trace("loading links");
            IList<LinkedRectangle> links = session.GetLinks();
            DocuLinkLog.Trace($"loaded links count={links.Count}");

            bool anyChanged = false;
            int scanned = 0;
            int missingMaps = 0;
            int missingRanges = 0;
            int changed = 0;

            foreach (LinkedRectangle linkedRect in links)
            {
                scanned++;
                int trackIndex = linkedRect.LinkedCell.TrackIndex;
                Excel.XmlMap map = FindMap(workbook, trackIndex);
                if (map == null)
                {
                    missingMaps++;
                    continue;
                }

                Excel.Range foundRange = FindRangeForMap(workbook, map);
                if (foundRange == null)
                {
                    missingRanges++;
                    continue;
                }

                string newSheet = ((Excel.Worksheet)foundRange.Worksheet).Name;
                string newAddress = foundRange.Address;

                if (!string.Equals(newSheet, linkedRect.LinkedCell.SheetName, StringComparison.Ordinal) ||
                    !string.Equals(newAddress, linkedRect.LinkedCell.Address, StringComparison.Ordinal))
                {
                    linkedRect.LinkedCell.SheetName = newSheet;
                    linkedRect.LinkedCell.Address = newAddress;
                    anyChanged = true;
                    changed++;
                }
            }

            DocuLinkLog.Trace($"scan done scanned={scanned} changed={changed} missingMaps={missingMaps} missingRanges={missingRanges}");

            if (anyChanged)
            {
                DocuLinkLog.Trace("saving updated link positions");
                session.SetLinks(links.ToList());
                DocuLinkLog.Trace("saved updated link positions");
            }

            DocuLinkLog.Trace("EXIT");
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static Excel.XmlMap EnsureMap(Excel.Workbook workbook, int trackIndex, out bool created)
        {
            Excel.XmlMap existing = FindMap(workbook, trackIndex);
            if (existing != null)
            {
                created = false;
                return existing;
            }

            Excel.XmlMap map = workbook.XmlMaps.Add(MapSchemaXml, Type.Missing);
            map.Name = MapNamePrefix + trackIndex;
            ConfigureMapFormatting(map);
            created = true;
            return map;
        }

        private static void ConfigureMapFormatting(Excel.XmlMap map)
        {
            if (map == null) return;

            try { map.PreserveNumberFormatting = true; }
            catch (COMException) { }

            try { map.AdjustColumnWidth = false; }
            catch (COMException) { }
        }

        private static Excel.XmlMap FindMap(Excel.Workbook workbook, int trackIndex)
        {
            string targetName = MapNamePrefix + trackIndex;
            foreach (Excel.XmlMap map in workbook.XmlMaps)
            {
                try
                {
                    if (string.Equals(map.Name, targetName, StringComparison.Ordinal))
                        return map;
                }
                catch (COMException) { }
            }
            return null;
        }

        /// <summary>
        /// Iterates all worksheets calling <c>XmlDataQuery</c> with <paramref name="map"/>
        /// and returns the first range found, or <c>null</c> if the binding is not present
        /// on any sheet.
        /// </summary>
        private static Excel.Range FindRangeForMap(Excel.Workbook workbook, Excel.XmlMap map)
        {
            foreach (Excel.Worksheet ws in workbook.Worksheets)
            {
                try
                {
                    object result = ws.XmlDataQuery(LinkXPath, Type.Missing, map);
                    if (result is Excel.Range range)
                        return range;
                }
                catch (COMException) { }
            }
            return null;
        }

        private static string GetWorkbookDebugName(Excel.Workbook workbook)
        {
            try
            {
                if (!string.IsNullOrEmpty(workbook.FullName))
                    return workbook.FullName;
            }
            catch (COMException ex)
            {
                DocuLinkLog.Trace($"FullName unavailable: {ex.Message}");
            }

            try
            {
                return workbook.Name ?? "(unnamed)";
            }
            catch (COMException ex)
            {
                DocuLinkLog.Trace($"Name unavailable: {ex.Message}");
                return "(workbook COM unavailable)";
            }
        }
    }
}
