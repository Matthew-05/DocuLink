using System;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace Talliark.Addin.Modules.Services
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
        private const int RevealContextBoundaryOffset = 2;
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
                TalliarkLog.Trace($"ExcelCellNavigationService.Navigate failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Talliark] ExcelCellNavigationService.Navigate failed: {ex.Message}");
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

        /// <summary>
        /// Ensures <paramref name="cell"/> is visible in the active worksheet window.
        /// By default, moves each axis only far enough to reveal the complete cell.
        /// </summary>
        /// <remarks>
        /// View movement is best-effort: failure to scroll must never fail an operation that
        /// has already created or updated workbook data.
        /// </remarks>
        public static void BringIntoView(Excel.Range cell, bool alignToTopLeft = false)
        {
            if (cell == null) return;

            try
            {
                var app = cell.Application as Excel.Application;
                var worksheet = cell.Worksheet as Excel.Worksheet;
                if (app == null || worksheet == null) return;

                worksheet.Activate();

                // The table-copy workflow intentionally keeps its established jump-to-cell
                // behavior. Interactive rectangle creation takes the native-style path below.
                if (alignToTopLeft)
                {
                    app.Goto(cell, true);
                    return;
                }

                Excel.Window window = app.ActiveWindow;
                if (window == null || IsVisibleInAnyPane(app, window, cell)) return;

                Excel.Pane pane = window.ActivePane;
                if (pane == null) return;

                bool restoreScreenUpdating = app.ScreenUpdating;
                try
                {
                    if (restoreScreenUpdating)
                        app.ScreenUpdating = false;

                    ScrollPaneMinimally(app, pane, cell);
                }
                finally
                {
                    if (restoreScreenUpdating)
                        app.ScreenUpdating = true;
                }
            }
            catch (COMException ex)
            {
                TalliarkLog.Trace($"ExcelCellNavigationService.BringIntoView failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Talliark] ExcelCellNavigationService.BringIntoView failed: {ex.Message}");
            }
        }

        private static bool IsVisibleInAnyPane(
            Excel.Application app, Excel.Window window, Excel.Range cell)
        {
            try
            {
                Excel.Panes panes = window.Panes;
                int paneCount = panes?.Count ?? 0;
                for (int index = 1; index <= paneCount; index++)
                {
                    Excel.Pane pane = panes[index];
                    if (IsVisible(app, pane, cell))
                        return true;
                }

                return paneCount == 0 && app.Intersect(cell, window.VisibleRange) != null;
            }
            catch (COMException)
            {
                return false;
            }
        }

        private static void ScrollPaneMinimally(
            Excel.Application app, Excel.Pane pane, Excel.Range cell)
        {
            Excel.Range visible = pane.VisibleRange;
            if (visible == null) return;

            var worksheet = cell.Worksheet as Excel.Worksheet;
            if (worksheet == null) return;

            int targetColumn = cell.Column;
            int maxColumn = worksheet.Columns.Count;
            int firstVisibleColumn = visible.Column;
            int lastVisibleColumn = firstVisibleColumn + visible.Columns.Count - 1;

            if (targetColumn < firstVisibleColumn)
            {
                pane.ScrollColumn = targetColumn;
            }
            else if (targetColumn > lastVisibleColumn)
            {
                // Advancing one column at a time finds the first scroll position that
                // fully reveals the target, even when intervening columns have different
                // widths. VisibleRange includes a partially clipped edge column; allowing
                // the second following column to begin appearing doubles the small amount
                // of context from the complete-cell reveal without repositioning the target.
                int columnVisibilitySentinel = Math.Min(
                    maxColumn, targetColumn + RevealContextBoundaryOffset);
                while (!IsColumnVisible(pane, columnVisibilitySentinel)
                    && pane.ScrollColumn < targetColumn)
                    pane.ScrollColumn++;
            }

            visible = pane.VisibleRange;
            int targetRow = cell.Row;
            int maxRow = worksheet.Rows.Count;
            int firstVisibleRow = visible.Row;
            int lastVisibleRow = firstVisibleRow + visible.Rows.Count - 1;

            if (targetRow < firstVisibleRow)
            {
                pane.ScrollRow = targetRow;
            }
            else if (targetRow > lastVisibleRow)
            {
                // Apply the same complete-cell rule vertically. The first glimpse of the
                // second following row provides the same modest extra context at the bottom.
                int rowVisibilitySentinel = Math.Min(
                    maxRow, targetRow + RevealContextBoundaryOffset);
                while (!IsRowVisible(pane, rowVisibilitySentinel) && pane.ScrollRow < targetRow)
                    pane.ScrollRow++;
            }
        }

        private static bool IsColumnVisible(Excel.Pane pane, int column)
        {
            Excel.Range visible = pane?.VisibleRange;
            if (visible == null) return false;

            int first = visible.Column;
            return column >= first && column < first + visible.Columns.Count;
        }

        private static bool IsRowVisible(Excel.Pane pane, int row)
        {
            Excel.Range visible = pane?.VisibleRange;
            if (visible == null) return false;

            int first = visible.Row;
            return row >= first && row < first + visible.Rows.Count;
        }

        private static bool IsVisible(
            Excel.Application app, Excel.Pane pane, Excel.Range cell)
        {
            Excel.Range visible = pane?.VisibleRange;
            return visible != null && app.Intersect(cell, visible) != null;
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
                TalliarkLog.Trace($"ExcelCellNavigationService select refused: {ex.Message}");
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
