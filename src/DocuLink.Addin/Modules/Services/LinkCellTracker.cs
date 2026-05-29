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

            Excel.XmlMap map = EnsureMap(workbook, trackIndex);
            cell.XPath.SetValue(map, LinkXPath, Type.Missing, false);
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
                Excel.XmlMap map = cell.XPath.Map;
                if (map == null) return 0;

                string name = map.Name;
                if (name == null || !name.StartsWith(MapNamePrefix, StringComparison.Ordinal))
                    return 0;

                string suffix = name.Substring(MapNamePrefix.Length);
                return int.TryParse(suffix, out int idx) && idx > 0 ? idx : 0;
            }
            catch (COMException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Queries each worksheet for the current range of every linked rectangle's
        /// XmlMap binding and updates the stored <see cref="LinkedCell.SheetName"/>
        /// and <see cref="LinkedCell.Address"/> in the workbook's Custom XML part.
        /// Call this on <c>WorkbookBeforeSave</c> to keep persisted addresses current.
        /// </summary>
        public static void SyncAllPositions(Excel.Workbook workbook)
        {
            if (workbook == null) return;
            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);
            IList<LinkedRectangle> links = session.GetLinks();
            bool anyChanged = false;

            foreach (LinkedRectangle linkedRect in links)
            {
                int trackIndex = linkedRect.LinkedCell.TrackIndex;
                Excel.XmlMap map = FindMap(workbook, trackIndex);
                if (map == null) continue;

                Excel.Range foundRange = FindRangeForMap(workbook, map);
                if (foundRange == null) continue;

                string newSheet = ((Excel.Worksheet)foundRange.Worksheet).Name;
                string newAddress = foundRange.get_Address(true, true);

                if (!string.Equals(newSheet, linkedRect.LinkedCell.SheetName, StringComparison.Ordinal) ||
                    !string.Equals(newAddress, linkedRect.LinkedCell.Address, StringComparison.Ordinal))
                {
                    linkedRect.LinkedCell.SheetName = newSheet;
                    linkedRect.LinkedCell.Address = newAddress;
                    anyChanged = true;
                }
            }

            if (anyChanged)
                session.SetLinks(links.ToList());
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static Excel.XmlMap EnsureMap(Excel.Workbook workbook, int trackIndex)
        {
            Excel.XmlMap existing = FindMap(workbook, trackIndex);
            if (existing != null) return existing;

            Excel.XmlMap map = workbook.XmlMaps.Add(MapSchemaXml, Type.Missing);
            map.Name = MapNamePrefix + trackIndex;
            return map;
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
    }
}
