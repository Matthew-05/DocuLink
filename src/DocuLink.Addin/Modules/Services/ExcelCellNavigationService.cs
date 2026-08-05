using System;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Applies Excel's own cell-navigation semantics to keystrokes forwarded from the
    /// viewer, so Tab and Enter move the grid cursor exactly as they would if the user
    /// had pressed them with the worksheet focused.
    /// </summary>
    /// <remarks>
    /// Three behaviours make this more than an offset:
    /// <list type="bullet">
    /// <item>Enter follows <c>Application.MoveAfterReturnDirection</c>, and does nothing
    /// at all when <c>MoveAfterReturn</c> is off.</item>
    /// <item>An Enter that ends a run of Tabs returns to the column the run started in,
    /// which is what makes tabbing across a row and pressing Enter drop to the start of
    /// the next row.</item>
    /// <item>Inside a multi-cell selection both motions cycle within the selection
    /// instead of collapsing it — Tab row-major, Enter column-major, both wrapping.</item>
    /// </list>
    /// The Tab-run anchor is tracked here rather than read from Excel because Excel does
    /// not expose it. It is invalidated whenever the active cell is not where the last
    /// navigation left it, which covers the user clicking elsewhere, switching sheets or
    /// creating a link, without needing to hook selection events.
    /// </remarks>
    internal sealed class ExcelCellNavigationService
    {
        private const int XlDown    = -4121;
        private const int XlUp      = -4162;
        private const int XlToRight = -4161;
        private const int XlToLeft  = -4159;

        private string _expectedSheet;
        private int _expectedRow;
        private int _expectedColumn;
        private int _anchorColumn;
        private bool _tabRunActive;

        internal enum Motion
        {
            Tab,
            Enter,
        }

        /// <summary>
        /// Moves the Excel cursor for one forwarded keystroke. Silently does nothing when
        /// Excel is unavailable or refuses the move (protected sheet, sheet edge).
        /// </summary>
        public void Navigate(Motion motion, bool reverse)
        {
            Excel.Application app = Globals.ThisAddIn.Application;
            if (app == null) return;

            try
            {
                var selection = app.Selection as Excel.Range;
                var activeCell = app.ActiveCell;
                if (selection == null || activeCell == null) return;

                var sheet = activeCell.Worksheet;
                if (sheet == null) return;

                InvalidateAnchorIfCursorMoved(sheet.Name, activeCell);

                using (Globals.ThisAddIn.EnterSelectionNavSuppress())
                {
                    if (TryNavigateWithinSelection(selection, activeCell, motion, reverse))
                        return;

                    NavigateSingleCell(app, sheet, activeCell, motion, reverse);
                }
            }
            catch (COMException ex)
            {
                DocuLinkLog.Trace($"ExcelCellNavigationService.Navigate failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] ExcelCellNavigationService.Navigate failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Drops the Tab-run anchor. Called when something other than navigation moves the
        /// cursor — creating a link, for instance — so the next Enter behaves as a fresh entry.
        /// </summary>
        public void ResetAnchor()
        {
            _tabRunActive = false;
            _expectedSheet = null;
        }

        private void InvalidateAnchorIfCursorMoved(string sheetName, Excel.Range activeCell)
        {
            if (!_tabRunActive) return;

            bool stillWhereWeLeftIt =
                string.Equals(_expectedSheet, sheetName, StringComparison.OrdinalIgnoreCase)
                && _expectedRow == activeCell.Row
                && _expectedColumn == activeCell.Column;

            if (!stillWhereWeLeftIt)
                ResetAnchor();
        }

        private void RememberCursor(Excel.Range cell)
        {
            try
            {
                _expectedSheet  = ((Excel.Worksheet)cell.Worksheet).Name;
                _expectedRow    = cell.Row;
                _expectedColumn = cell.Column;
            }
            catch (COMException)
            {
                ResetAnchor();
            }
        }

        /// <summary>
        /// Cycles the active cell inside a multi-cell selection, leaving the selection intact.
        /// Returns <c>false</c> when the selection is a single cell or spans several areas,
        /// in which case the caller falls back to a plain directional move.
        /// </summary>
        private bool TryNavigateWithinSelection(
            Excel.Range selection, Excel.Range activeCell, Motion motion, bool reverse)
        {
            int areaCount;
            try { areaCount = selection.Areas?.Count ?? 1; }
            catch (COMException) { areaCount = 1; }

            // Multi-area selections cycle across areas in Excel. That is rare enough from a
            // PDF-drawing workflow that a plain directional move is the better trade.
            if (areaCount != 1) return false;

            int rows = selection.Rows.Count;
            int cols = selection.Columns.Count;
            int total = rows * cols;
            if (total <= 1) return false;

            int rowOffset = activeCell.Row - selection.Row;
            int colOffset = activeCell.Column - selection.Column;

            // The active cell can sit outside the selection after certain Excel operations.
            if (rowOffset < 0 || rowOffset >= rows || colOffset < 0 || colOffset >= cols)
                return false;

            int step = reverse ? -1 : 1;
            int nextRow, nextCol;

            if (motion == Motion.Tab)
            {
                int index = (rowOffset * cols) + colOffset;
                int next = Wrap(index + step, total);
                nextRow = next / cols;
                nextCol = next % cols;
            }
            else
            {
                int index = (colOffset * rows) + rowOffset;
                int next = Wrap(index + step, total);
                nextCol = next / rows;
                nextRow = next % rows;
            }

            // Activate (not Select) keeps the surrounding selection in place, which is how
            // Excel itself walks the cursor through a highlighted block.
            var target = (Excel.Range)selection.Cells[nextRow + 1, nextCol + 1];
            target.Activate();

            // A Tab run inside a selection has no meaningful anchor column to return to,
            // because Enter already wraps within the block.
            ResetAnchor();
            return true;
        }

        private void NavigateSingleCell(
            Excel.Application app, Excel.Worksheet sheet, Excel.Range activeCell, Motion motion, bool reverse)
        {
            int rowDelta = 0;
            int colDelta = 0;
            bool returnToAnchor = false;

            if (motion == Motion.Tab)
            {
                // Tab is always horizontal in Excel, regardless of the Enter direction setting.
                if (!_tabRunActive)
                {
                    _anchorColumn = activeCell.Column;
                    _tabRunActive = true;
                }

                colDelta = reverse ? -1 : 1;
            }
            else
            {
                if (!MoveAfterReturnEnabled(app))
                {
                    _tabRunActive = false;
                    return;
                }

                switch (MoveAfterReturnDirection(app))
                {
                    case XlUp:      rowDelta = reverse ?  1 : -1; break;
                    case XlToRight: colDelta = reverse ? -1 :  1; break;
                    case XlToLeft:  colDelta = reverse ?  1 : -1; break;
                    default:        rowDelta = reverse ? -1 :  1; break; // xlDown
                }

                // Only a vertical Enter returns to the column the Tab run started in.
                returnToAnchor = _tabRunActive && rowDelta != 0;
                _tabRunActive = false;
            }

            int targetRow = Clamp(activeCell.Row + rowDelta, sheet.Rows.Count);
            int targetCol = returnToAnchor
                ? Clamp(_anchorColumn, sheet.Columns.Count)
                : Clamp(activeCell.Column + colDelta, sheet.Columns.Count);

            if (targetRow == activeCell.Row && targetCol == activeCell.Column)
                return;

            try
            {
                var target = (Excel.Range)sheet.Cells[targetRow, targetCol];
                target.Select();
                RememberCursor(target);
            }
            catch (COMException ex)
            {
                // Protected sheets can refuse selection of locked cells. Leave the cursor put.
                DocuLinkLog.Trace($"ExcelCellNavigationService select refused: {ex.Message}");
                ResetAnchor();
            }
        }

        private static bool MoveAfterReturnEnabled(Excel.Application app)
        {
            try { return app.MoveAfterReturn; }
            catch (COMException) { return true; }
        }

        private static int MoveAfterReturnDirection(Excel.Application app)
        {
            try { return (int)app.MoveAfterReturnDirection; }
            catch (COMException) { return XlDown; }
        }

        private static int Wrap(int index, int total) => ((index % total) + total) % total;

        private static int Clamp(int value, int max) => value < 1 ? 1 : (value > max ? max : value);
    }
}
