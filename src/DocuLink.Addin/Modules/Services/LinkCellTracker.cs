using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using DocuLink.Addin.Modules;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Tracks linked cells through direct-reference formulas stored on an
    /// <c>xlSheetVeryHidden</c> worksheet. Excel rewrites those references for
    /// cut/paste moves, inserted or deleted rows and columns, and worksheet renames,
    /// without imposing the filtering restrictions of mapped XML cells.
    /// </summary>
    internal static class LinkCellTracker
    {
        private const string TrackerSheetBaseName = "_DocuLinkTracking";
        private const string TrackerSheetSentinel = "DocuLink Formula Tracking v1";
        private const string LegacyMapNamePrefix = "DocuLink_";
        private const string LegacyLinkXPath = "/DocuLinkLink";
        private const int TrackIndexColumn = 1;
        private const int FormulaColumn = 2;
        private const int FirstTrackerRow = 2;
        private const int MaximumTrackIndex = 1048574;

        public static int NextTrackIndex(IEnumerable<LinkedRectangle> linkedRectangles)
        {
            if (linkedRectangles == null) throw new ArgumentNullException(nameof(linkedRectangles));

            int max = 0;
            foreach (LinkedRectangle rectangle in linkedRectangles)
                if (rectangle.LinkedCell.TrackIndex > max)
                    max = rectangle.LinkedCell.TrackIndex;

            return checked(max + 1);
        }

        public static void BindCell(Excel.Workbook workbook, Excel.Range cell, int trackIndex)
        {
            ValidateBindingArguments(workbook, cell, trackIndex);
            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            ExecuteWorkbookMutation(workbook, () =>
            {
                Excel.Worksheet trackerSheet = EnsureTrackingSheet(workbook);
                WriteBinding(trackerSheet, cell, trackIndex);
                RemoveLegacyMap(workbook, trackIndex);
            });
        }

        public static void UnbindCell(Excel.Workbook workbook, Excel.Range cell, int trackIndex)
        {
            if (trackIndex <= 0) return;
            if (workbook != null)
                WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);
            if (workbook == null) return;

            ExecuteWorkbookMutation(workbook, () =>
            {
                Excel.Worksheet trackerSheet = FindTrackingSheet(workbook);
                if (trackerSheet != null && trackIndex <= MaximumTrackIndex)
                {
                    int row = GetTrackerRow(trackIndex);
                    ((Excel.Range)trackerSheet.Cells[row, TrackIndexColumn]).ClearContents();
                    ((Excel.Range)trackerSheet.Cells[row, FormulaColumn]).ClearContents();
                    DeleteTrackingSheetIfEmpty(trackerSheet);
                }

                RemoveLegacyMap(workbook, trackIndex);
            });
        }

        /// <summary>
        /// Creates formula trackers for persisted links that do not have one, then removes
        /// the old XML maps. Existing broken formula references are not rebuilt from their
        /// stored address because <c>#REF!</c> means the tracked cell was deleted.
        /// </summary>
        public static void EnsureBindings(
            Excel.Workbook workbook,
            IEnumerable<LinkedRectangle> linkedRectangles)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (linkedRectangles == null) throw new ArgumentNullException(nameof(linkedRectangles));

            List<LinkedRectangle> distinctLinks = linkedRectangles
                .Where(link => link?.LinkedCell != null && link.LinkedCell.TrackIndex > 0)
                .GroupBy(link => link.LinkedCell.TrackIndex)
                .Select(group => group.First())
                .ToList();
            var liveTrackIndexes = new HashSet<int>(
                distinctLinks.Select(link => link.LinkedCell.TrackIndex));
            Excel.Worksheet trackerSheet = FindTrackingSheet(workbook);

            bool hasMissingBindings = distinctLinks.Any(link =>
                !BindingExists(trackerSheet, link.LinkedCell.TrackIndex));
            bool hasOrphanBindings = trackerSheet != null
                && FindStoredTrackIndexes(trackerSheet).Any(index => !liveTrackIndexes.Contains(index));
            bool visibilityNeedsRepair = trackerSheet != null
                && trackerSheet.Visible != Excel.XlSheetVisibility.xlSheetVeryHidden;
            bool hasLegacyMaps = HasLegacyMaps(workbook);

            if (!hasMissingBindings && !hasOrphanBindings
                && !visibilityNeedsRepair && !hasLegacyMaps)
                return;

            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            ExecuteWorkbookMutation(workbook, () =>
            {
                trackerSheet = FindTrackingSheet(workbook);

                foreach (LinkedRectangle link in distinctLinks)
                {
                    int trackIndex = link.LinkedCell.TrackIndex;
                    if (BindingExists(trackerSheet, trackIndex))
                        continue;

                    Excel.Range target = FindRangeForLegacyMap(
                        workbook,
                        FindLegacyMap(workbook, trackIndex));
                    if (target == null)
                        target = ResolveStoredCell(workbook, link.LinkedCell);
                    if (target == null)
                        continue;

                    trackerSheet = trackerSheet ?? EnsureTrackingSheet(workbook);
                    WriteBinding(trackerSheet, target, trackIndex);
                }

                if (trackerSheet != null)
                {
                    foreach (int orphanTrackIndex in FindStoredTrackIndexes(trackerSheet)
                        .Where(index => !liveTrackIndexes.Contains(index))
                        .ToList())
                    {
                        int row = GetTrackerRow(orphanTrackIndex);
                        ((Excel.Range)trackerSheet.Cells[row, TrackIndexColumn]).ClearContents();
                        ((Excel.Range)trackerSheet.Cells[row, FormulaColumn]).ClearContents();
                    }

                    EnsureTrackingSheetHidden(workbook, trackerSheet);
                    DeleteTrackingSheetIfEmpty(trackerSheet);
                }

                RemoveAllLegacyMaps(workbook);
            });
        }

        public sealed class TrackedCell
        {
            public TrackedCell(int trackIndex, Excel.Range cell)
            {
                TrackIndex = trackIndex;
                Cell = cell;
            }

            public int TrackIndex { get; }

            public Excel.Range Cell { get; }
        }

        public static IList<TrackedCell> FindTrackedCellsInRange(Excel.Range target)
        {
            var found = new List<TrackedCell>();
            if (target == null) return found;

            Excel.Workbook workbook;
            Excel.Application application;
            try
            {
                var worksheet = target.Worksheet as Excel.Worksheet;
                workbook = worksheet?.Parent as Excel.Workbook;
                application = workbook?.Application as Excel.Application;
                if (workbook == null || application == null) return found;
            }
            catch (COMException)
            {
                return found;
            }

            Excel.Worksheet trackerSheet = FindTrackingSheet(workbook);
            if (trackerSheet == null) return found;

            foreach (int trackIndex in FindStoredTrackIndexes(trackerSheet))
            {
                Excel.Range trackedCell = TryResolveCell(workbook, trackIndex, out _);
                if (trackedCell == null) continue;

                try
                {
                    if (application.Intersect(trackedCell, target) == null) continue;
                }
                catch (COMException)
                {
                    continue;
                }

                found.Add(new TrackedCell(trackIndex, trackedCell));
            }

            found.Sort((left, right) =>
            {
                try
                {
                    int rowCompare = left.Cell.Row.CompareTo(right.Cell.Row);
                    return rowCompare != 0
                        ? rowCompare
                        : left.Cell.Column.CompareTo(right.Cell.Column);
                }
                catch (COMException)
                {
                    return 0;
                }
            });

            return found;
        }

        public static int FindTrackIndexForCell(Excel.Range cell)
        {
            IList<TrackedCell> tracked = FindTrackedCellsInRange(cell);
            return tracked.Count > 0 ? tracked[0].TrackIndex : 0;
        }

        internal static Excel.Range TryResolveCell(
            Excel.Workbook workbook,
            int trackIndex,
            out bool bindingExists)
        {
            bindingExists = false;
            if (workbook == null || trackIndex <= 0 || trackIndex > MaximumTrackIndex)
                return null;

            Excel.Worksheet trackerSheet = FindTrackingSheet(workbook);
            if (trackerSheet == null) return null;

            Excel.Range formulaCell =
                (Excel.Range)trackerSheet.Cells[GetTrackerRow(trackIndex), FormulaColumn];
            string formula;
            try
            {
                formula = Convert.ToString(formulaCell.Formula, CultureInfo.InvariantCulture);
            }
            catch (COMException)
            {
                return null;
            }

            bindingExists = !string.IsNullOrWhiteSpace(formula)
                && formula.StartsWith("=", StringComparison.Ordinal);
            if (!bindingExists) return null;

            return ResolveReferenceFormula(workbook, formula);
        }

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

                WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);
                IList<LinkedRectangle> links = session.GetLinks();
                EnsureBindings(workbook, links);

                bool anyChanged = false;
                int scanned = 0;
                int missingBindings = 0;
                int brokenReferences = 0;
                int changed = 0;

                foreach (LinkedRectangle linkedRectangle in links)
                {
                    scanned++;
                    Excel.Range foundRange = TryResolveCell(
                        workbook,
                        linkedRectangle.LinkedCell.TrackIndex,
                        out bool bindingExists);
                    if (foundRange == null)
                    {
                        if (bindingExists) brokenReferences++;
                        else missingBindings++;
                        continue;
                    }

                    string newSheet = ((Excel.Worksheet)foundRange.Worksheet).Name;
                    string newAddress = foundRange.Address;
                    if (string.Equals(newSheet, linkedRectangle.LinkedCell.SheetName, StringComparison.Ordinal)
                        && string.Equals(newAddress, linkedRectangle.LinkedCell.Address, StringComparison.Ordinal))
                        continue;

                    linkedRectangle.LinkedCell.SheetName = newSheet;
                    linkedRectangle.LinkedCell.Address = newAddress;
                    anyChanged = true;
                    changed++;
                }

                DocuLinkLog.Trace(
                    $"scan done scanned={scanned} changed={changed} "
                    + $"missingBindings={missingBindings} brokenReferences={brokenReferences}");

                if (anyChanged)
                    session.SetLinks(links.ToList());
            }

            DocuLinkLog.Trace("EXIT");
        }

        private static void ValidateBindingArguments(
            Excel.Workbook workbook,
            Excel.Range cell,
            int trackIndex)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (cell == null) throw new ArgumentNullException(nameof(cell));
            if (trackIndex <= 0 || trackIndex > MaximumTrackIndex)
                throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }

        private static int GetTrackerRow(int trackIndex)
        {
            return checked(trackIndex + 1);
        }

        private static void WriteBinding(
            Excel.Worksheet trackerSheet,
            Excel.Range target,
            int trackIndex)
        {
            int row = GetTrackerRow(trackIndex);
            ((Excel.Range)trackerSheet.Cells[row, TrackIndexColumn]).Value2 = trackIndex;
            ((Excel.Range)trackerSheet.Cells[row, FormulaColumn]).Formula =
                BuildReferenceFormula(target);
        }

        private static string BuildReferenceFormula(Excel.Range target)
        {
            string sheetName = ((Excel.Worksheet)target.Worksheet).Name;
            string escapedSheetName = sheetName.Replace("'", "''");
            return $"='{escapedSheetName}'!{target.Address}";
        }

        private static Excel.Range ResolveReferenceFormula(
            Excel.Workbook workbook,
            string formula)
        {
            if (string.IsNullOrWhiteSpace(formula)
                || !formula.StartsWith("=", StringComparison.Ordinal)
                || formula.IndexOf("#REF!", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            string reference = formula.Substring(1).Trim();
            int separator = reference.LastIndexOf('!');
            if (separator <= 0 || separator >= reference.Length - 1)
                return null;

            string sheetToken = reference.Substring(0, separator).Trim();
            string address = reference.Substring(separator + 1).Trim();
            if (address.StartsWith("@", StringComparison.Ordinal))
                address = address.Substring(1);

            if (sheetToken.StartsWith("'", StringComparison.Ordinal)
                && sheetToken.EndsWith("'", StringComparison.Ordinal)
                && sheetToken.Length >= 2)
            {
                sheetToken = sheetToken.Substring(1, sheetToken.Length - 2)
                    .Replace("''", "'");
            }

            if (sheetToken.IndexOf("[", StringComparison.Ordinal) >= 0
                || sheetToken.IndexOf("]", StringComparison.Ordinal) >= 0)
                return null;

            Excel.Worksheet worksheet = FindWorksheet(workbook, sheetToken);
            if (worksheet == null) return null;

            try
            {
                return worksheet.Range[address] as Excel.Range;
            }
            catch (COMException)
            {
                return null;
            }
        }

        private static Excel.Range ResolveStoredCell(
            Excel.Workbook workbook,
            LinkedCell linkedCell)
        {
            if (linkedCell == null) return null;

            Excel.Worksheet worksheet = FindWorksheet(workbook, linkedCell.SheetName);
            if (worksheet == null) return null;

            try
            {
                return worksheet.Range[linkedCell.Address] as Excel.Range;
            }
            catch (COMException)
            {
                return null;
            }
        }

        private static Excel.Worksheet FindWorksheet(
            Excel.Workbook workbook,
            string sheetName)
        {
            if (string.IsNullOrWhiteSpace(sheetName)) return null;

            foreach (Excel.Worksheet worksheet in workbook.Worksheets)
            {
                try
                {
                    if (string.Equals(worksheet.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                        return worksheet;
                }
                catch (COMException)
                {
                }
            }

            return null;
        }

        private static Excel.Worksheet FindTrackingSheet(Excel.Workbook workbook)
        {
            foreach (Excel.Worksheet worksheet in workbook.Worksheets)
            {
                try
                {
                    string sentinel = Convert.ToString(
                        ((Excel.Range)worksheet.Cells[1, 1]).Value2,
                        CultureInfo.InvariantCulture);
                    if (string.Equals(sentinel, TrackerSheetSentinel, StringComparison.Ordinal))
                        return worksheet;
                }
                catch (COMException)
                {
                }
            }

            return null;
        }

        private static Excel.Worksheet EnsureTrackingSheet(Excel.Workbook workbook)
        {
            Excel.Worksheet existing = FindTrackingSheet(workbook);
            if (existing != null)
            {
                EnsureTrackingSheetHidden(workbook, existing);
                return existing;
            }

            Excel.Worksheet previouslyActive = workbook.ActiveSheet as Excel.Worksheet;
            object after = workbook.Worksheets[workbook.Worksheets.Count];
            var trackerSheet = (Excel.Worksheet)workbook.Worksheets.Add(
                Type.Missing,
                after,
                Type.Missing,
                Type.Missing);
            trackerSheet.Name = FindAvailableTrackerSheetName(workbook);
            ((Excel.Range)trackerSheet.Cells[1, 1]).Value2 = TrackerSheetSentinel;
            ((Excel.Range)trackerSheet.Cells[1, 2]).Value2 = "Reference";

            try { previouslyActive?.Activate(); }
            catch (COMException) { }

            EnsureTrackingSheetHidden(workbook, trackerSheet);
            return trackerSheet;
        }

        private static string FindAvailableTrackerSheetName(Excel.Workbook workbook)
        {
            for (int suffix = 0; suffix < 1000; suffix++)
            {
                string candidate = suffix == 0
                    ? TrackerSheetBaseName
                    : TrackerSheetBaseName + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                if (FindWorksheet(workbook, candidate) == null)
                    return candidate;
            }

            throw new InvalidOperationException("Unable to allocate a DocuLink tracking worksheet name.");
        }

        private static void EnsureTrackingSheetHidden(
            Excel.Workbook workbook,
            Excel.Worksheet trackerSheet)
        {
            if (trackerSheet.Visible == Excel.XlSheetVisibility.xlSheetVeryHidden)
                return;

            try
            {
                string activeName = (workbook.ActiveSheet as Excel.Worksheet)?.Name;
                if (string.Equals(activeName, trackerSheet.Name, StringComparison.Ordinal))
                {
                    foreach (Excel.Worksheet worksheet in workbook.Worksheets)
                    {
                        if (string.Equals(worksheet.Name, trackerSheet.Name, StringComparison.Ordinal))
                            continue;
                        if (worksheet.Visible != Excel.XlSheetVisibility.xlSheetVisible)
                            continue;
                        worksheet.Activate();
                        break;
                    }
                }
            }
            catch (COMException)
            {
            }

            trackerSheet.Visible = Excel.XlSheetVisibility.xlSheetVeryHidden;
        }

        private static bool BindingExists(Excel.Worksheet trackerSheet, int trackIndex)
        {
            if (trackerSheet == null || trackIndex <= 0 || trackIndex > MaximumTrackIndex)
                return false;

            try
            {
                string formula = Convert.ToString(
                    ((Excel.Range)trackerSheet.Cells[GetTrackerRow(trackIndex), FormulaColumn]).Formula,
                    CultureInfo.InvariantCulture);
                return !string.IsNullOrWhiteSpace(formula)
                    && formula.StartsWith("=", StringComparison.Ordinal);
            }
            catch (COMException)
            {
                return false;
            }
        }

        private static IList<int> FindStoredTrackIndexes(Excel.Worksheet trackerSheet)
        {
            var indexes = new List<int>();
            if (trackerSheet == null) return indexes;

            int lastRow;
            try
            {
                Excel.Range usedRange = trackerSheet.UsedRange;
                lastRow = usedRange.Row + usedRange.Rows.Count - 1;
            }
            catch (COMException)
            {
                return indexes;
            }

            for (int row = FirstTrackerRow; row <= lastRow; row++)
            {
                try
                {
                    object raw = ((Excel.Range)trackerSheet.Cells[row, TrackIndexColumn]).Value2;
                    if (raw == null) continue;
                    int trackIndex = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                    if (trackIndex > 0 && trackIndex <= MaximumTrackIndex)
                        indexes.Add(trackIndex);
                }
                catch (Exception ex) when (ex is COMException || ex is FormatException
                    || ex is InvalidCastException || ex is OverflowException)
                {
                }
            }

            return indexes;
        }

        private static void DeleteTrackingSheetIfEmpty(Excel.Worksheet trackerSheet)
        {
            if (trackerSheet == null || FindStoredTrackIndexes(trackerSheet).Count > 0)
                return;

            // Excel rejects Delete on an xlSheetVeryHidden worksheet. Mutations run with
            // screen updating and events disabled, so briefly revealing it is not visible
            // to the user and does not disturb the active worksheet.
            trackerSheet.Visible = Excel.XlSheetVisibility.xlSheetVisible;
            trackerSheet.Delete();
        }

        private static bool HasLegacyMaps(Excel.Workbook workbook)
        {
            foreach (Excel.XmlMap map in workbook.XmlMaps)
            {
                try
                {
                    if (map.Name?.StartsWith(LegacyMapNamePrefix, StringComparison.Ordinal) == true)
                        return true;
                }
                catch (COMException)
                {
                }
            }

            return false;
        }

        private static Excel.XmlMap FindLegacyMap(Excel.Workbook workbook, int trackIndex)
        {
            string targetName = LegacyMapNamePrefix + trackIndex.ToString(CultureInfo.InvariantCulture);
            foreach (Excel.XmlMap map in workbook.XmlMaps)
            {
                try
                {
                    if (string.Equals(map.Name, targetName, StringComparison.Ordinal))
                        return map;
                }
                catch (COMException)
                {
                }
            }

            return null;
        }

        private static Excel.Range FindRangeForLegacyMap(
            Excel.Workbook workbook,
            Excel.XmlMap map)
        {
            if (map == null) return null;

            foreach (Excel.Worksheet worksheet in workbook.Worksheets)
            {
                try
                {
                    object result = worksheet.XmlDataQuery(
                        LegacyLinkXPath,
                        Type.Missing,
                        map);
                    if (result is Excel.Range range)
                        return range;
                }
                catch (COMException)
                {
                }
            }

            return null;
        }

        private static void RemoveLegacyMap(Excel.Workbook workbook, int trackIndex)
        {
            Excel.XmlMap map = FindLegacyMap(workbook, trackIndex);
            if (map == null) return;

            Excel.Range mappedRange = FindRangeForLegacyMap(workbook, map);
            if (mappedRange != null)
            {
                try { mappedRange.XPath.Clear(); }
                catch (COMException) { }
            }

            try { map.Delete(); }
            catch (COMException) { }
        }

        private static void RemoveAllLegacyMaps(Excel.Workbook workbook)
        {
            for (int index = workbook.XmlMaps.Count; index >= 1; index--)
            {
                Excel.XmlMap map;
                try { map = workbook.XmlMaps[index]; }
                catch (COMException) { continue; }

                string name;
                try { name = map.Name; }
                catch (COMException) { continue; }
                if (name?.StartsWith(LegacyMapNamePrefix, StringComparison.Ordinal) != true)
                    continue;

                Excel.Range mappedRange = FindRangeForLegacyMap(workbook, map);
                if (mappedRange != null)
                {
                    try { mappedRange.XPath.Clear(); }
                    catch (COMException) { }
                }

                try { map.Delete(); }
                catch (COMException) { }
            }
        }

        private static void ExecuteWorkbookMutation(Excel.Workbook workbook, Action action)
        {
            Excel.Application application = workbook.Application as Excel.Application;
            if (application == null)
            {
                action();
                return;
            }

            bool previousEnableEvents = application.EnableEvents;
            bool previousDisplayAlerts = application.DisplayAlerts;
            bool previousScreenUpdating = application.ScreenUpdating;

            try
            {
                application.EnableEvents = false;
                application.DisplayAlerts = false;
                application.ScreenUpdating = false;
                action();
            }
            finally
            {
                try { application.ScreenUpdating = previousScreenUpdating; }
                catch (COMException) { }
                try { application.DisplayAlerts = previousDisplayAlerts; }
                catch (COMException) { }
                try { application.EnableEvents = previousEnableEvents; }
                catch (COMException) { }
            }
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
