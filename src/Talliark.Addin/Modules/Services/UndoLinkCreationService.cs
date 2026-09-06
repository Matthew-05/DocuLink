using System;
using System.Linq;
using System.Runtime.InteropServices;
using Talliark.Addin.Modules.CustomXml;
using Talliark.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace Talliark.Addin.Modules.Services
{
    /// <summary>
    /// Reverses the most recent link-rectangle creation recorded on the workbook's
    /// <see cref="LinkCreationUndoStack"/>.
    /// </summary>
    /// <remarks>
    /// Creation is the only undoable action, and the interactive create path only ever
    /// writes to a cell it found empty — so reversing it never has to restore displaced
    /// data. The one exception is a Sum rectangle appended to an existing Sum cell, which
    /// <see cref="DeleteLinkService"/> already handles by rebuilding the formula from the
    /// rectangles that remain. Undo therefore delegates the removal wholesale and only adds
    /// what deletion deliberately leaves behind: clearing the cell when no links are left on
    /// it, and restoring the number format the cell had before.
    /// </remarks>
    internal sealed class UndoLinkCreationService
    {
        /// <summary>
        /// Attempts only the newest entry. If its rectangle or cell has changed, the entry is
        /// discarded and undo stops rather than reaching backward to an older creation the user
        /// did not ask to reverse. Returns the id removed, or <c>null</c> when nothing was undone.
        /// </summary>
        public string UndoLast(Excel.Workbook workbook)
        {
            if (workbook == null) return null;

            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            LinkCreationUndoStack stack = Globals.ThisAddIn.GetLinkUndoStack(workbook);
            if (stack == null || stack.IsEmpty) return null;

            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);

            if (!stack.TryPop(out LinkCreationUndoEntry entry))
                return null;

            if (!TryResolveTarget(
                workbook, session, entry, out LinkedRectangle rect, out Excel.Range cell))
                return null;

            return UndoEntry(workbook, session, entry, rect, cell)
                ? entry.RectId
                : null;
        }

        /// <summary>
        /// Validates an entry against the live workbook. Anything that no longer lines up —
        /// a rectangle already deleted, a cell whose row was removed, a value the user has
        /// since edited — is treated as stale and ends this undo attempt, per the rule that
        /// undo must never overwrite a later change or reach backward to an older creation.
        /// </summary>
        private static bool TryResolveTarget(
            Excel.Workbook workbook,
            WorkbookStorageSession session,
            LinkCreationUndoEntry entry,
            out LinkedRectangle rect,
            out Excel.Range cell)
        {
            rect = null;
            cell = null;

            if (entry == null || string.IsNullOrEmpty(entry.RectId))
                return false;

            if (!session.TryGetLink(entry.RectId, out rect) || rect == null)
            {
                TalliarkLog.Trace($"undo: rect {entry.RectId} already gone – stopping");
                return false;
            }

            cell = LinkCellResolver.TryResolveCell(workbook, rect);
            if (cell == null)
            {
                TalliarkLog.Trace($"undo: cell for rect {entry.RectId} no longer resolves – stopping");
                return false;
            }

            try
            {
                var current = cell.Formula as string;
                if (!string.Equals(current, entry.ExpectedFormula, StringComparison.Ordinal))
                {
                    TalliarkLog.Trace(
                        $"undo: cell {cell.Address} changed since creation " +
                        $"(expected '{entry.ExpectedFormula}', found '{current}') – stopping");
                    return false;
                }
            }
            catch (COMException ex)
            {
                TalliarkLog.Trace($"undo: cell read failed – stopping: {ex.Message}");
                return false;
            }

            return true;
        }

        /// <remarks>
        /// The whole reversal runs under <see cref="ThisAddIn.EnterSelectionNavSuppress"/>. Beyond
        /// keeping the viewer from chasing the cursor, this is what marks the cell writes below as
        /// Talliark's rather than the user's — an unsuppressed <c>ClearContents</c> raises
        /// SheetChange, which the host reads as a user edit and uses to hand Ctrl+Z back to Excel,
        /// stopping any further undo dead.
        /// </remarks>
        private bool UndoEntry(
            Excel.Workbook workbook,
            WorkbookStorageSession session,
            LinkCreationUndoEntry entry,
            LinkedRectangle rect,
            Excel.Range cell)
        {
            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                return UndoEntryCore(workbook, session, entry, rect, cell);
            }
        }

        private bool UndoEntryCore(
            Excel.Workbook workbook,
            WorkbookStorageSession session,
            LinkCreationUndoEntry entry,
            LinkedRectangle rect,
            Excel.Range cell)
        {
            // Whether other rectangles still feed this cell decides what deletion leaves
            // behind: a Sum cell with siblings keeps a rebuilt formula, a cell without them
            // keeps the value the creation wrote and needs clearing here.
            bool hasSiblings = session
                .GetLinks()
                .Any(r => !string.Equals(r.Id, entry.RectId, StringComparison.Ordinal)
                          && r.LinkedCell.TrackIndex == rect.LinkedCell.TrackIndex);

            // Undo removes data produced by the creation being reversed. Keep this explicit
            // so ordinary link-removal callers remain preserve-data by default.
            if (!new DeleteLinkService().DeleteLink(
                entry.RectId,
                workbook,
                deleteCellData: true))
            {
                TalliarkLog.Trace($"undo: DeleteLink refused rect {entry.RectId}");
                return false;
            }

            if (!hasSiblings)
                ClearCell(cell, entry.PreviousNumberFormat);

            SelectClearedCell(cell);
            return true;
        }

        private static void ClearCell(Excel.Range cell, string previousNumberFormat)
        {
            try
            {
                cell.ClearContents();

                // Creation applied an inferred date or currency format. Put back whatever the
                // cell carried before rather than flattening it to General, which would lose
                // formatting the user had set on the empty cell.
                if (!string.IsNullOrEmpty(previousNumberFormat))
                    cell.NumberFormat = previousNumberFormat;
            }
            catch (COMException ex)
            {
                TalliarkLog.Trace($"undo: clearing cell failed: {ex.Message}");
            }
        }

        private static void SelectClearedCell(Excel.Range cell)
        {
            try
            {
                using (Globals.ThisAddIn.EnterSelectionNavSuppress())
                {
                    ((Excel.Worksheet)cell.Worksheet).Activate();
                    cell.Select();
                }
            }
            catch (COMException ex)
            {
                TalliarkLog.Trace($"undo: selecting cleared cell failed: {ex.Message}");
            }
        }
    }
}
