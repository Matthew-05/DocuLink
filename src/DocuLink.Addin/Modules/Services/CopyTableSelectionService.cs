using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Copies one Table rectangle to other PDF pages and stacks each extracted table in
    /// a separate Excel footprint immediately below the source table. Columns remain fixed
    /// to the source selection while every target supplies its independently detected rows.
    /// </summary>
    internal sealed class CopyTableSelectionService
    {
        public IList<LinkedRectangle> Copy(
            string sourceRectId,
            IList<(int Page, TableGrid Grid, IList<IList<string>> Cells)> targets,
            IWin32Window owner,
            Excel.Workbook workbook,
            Action<LinkedRectangle> onCopied = null)
        {
            if (string.IsNullOrWhiteSpace(sourceRectId) || workbook == null || targets == null)
                return null;

            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);
            WorkbookStorageSession session = Globals.ThisAddIn.GetStorageSession(workbook);
            if (!session.TryGetLink(sourceRectId, out LinkedRectangle source)
                || source.LinkType != LinkType.Table
                || source.TableGrid == null)
                return null;

            Excel.Range sourceAnchor = LinkCellResolver.TryResolveCell(workbook, source);
            if (sourceAnchor == null) return null;

            var orderedTargets = targets
                .Where(target => target.Page >= 0
                    && target.Page != source.Rectangle.PageIndex
                    && target.Grid != null
                    && target.Cells != null)
                .GroupBy(target => target.Page)
                .Select(group =>
                {
                    (int Page, TableGrid Grid, IList<IList<string>> Cells) target = group.First();
                    return (
                        target.Page,
                        Grid: MergeGrid(source.TableGrid, target.Grid),
                        target.Cells);
                })
                .OrderBy(target => target.Page)
                .ToList();
            if (orderedTargets.Count == 0) return null;

            var writer = new TableExcelWriteService();
            Excel.Range firstAnchor = sourceAnchor.get_Offset(source.TableGrid.RowCount, 0);
            var tables = orderedTargets
                .Select(target => (target.Grid, target.Cells))
                .ToList();
            if (!writer.ConfirmCopy(firstAnchor, tables, owner))
                return null;

            int totalRows = 0;
            foreach (var target in orderedTargets)
                totalRows = checked(totalRows + target.Grid.RowCount);
            Excel.Range combinedFootprint = TableExcelWriteService.GetFootprint(
                firstAnchor, totalRows, source.TableGrid.ColumnCount);
            IList<string> replacedIds =
                new DeleteLinkService().DeleteLinksInSelection(combinedFootprint, workbook);
            Globals.ThisAddIn.GetLinkUndoStack(workbook)?.DropEntriesFor(replacedIds);

            int nextTrackIndex = LinkCellTracker.NextTrackIndex(session.GetLinks());
            int rowOffset = 0;
            var copiedLinks = new List<LinkedRectangle>(orderedTargets.Count);
            try
            {
                for (int index = 0; index < orderedTargets.Count; index++)
                {
                    (int Page, TableGrid Grid, IList<IList<string>> Cells) target = orderedTargets[index];
                    Excel.Range targetAnchor = firstAnchor.get_Offset(rowOffset, 0);
                    string sheetName = ((Excel.Worksheet)targetAnchor.Worksheet).Name;
                    var linkedCell = new LinkedCell(sheetName, targetAnchor.Address, nextTrackIndex++);
                    var rectangle = new PdfRectangle(
                        target.Page,
                        source.Rectangle.X,
                        source.Rectangle.Y,
                        source.Rectangle.Width,
                        source.Rectangle.Height,
                        RectangleCoordinateSpace.Normalized);
                    var copiedLink = new LinkedRectangle(
                        Guid.NewGuid().ToString("D"), source.PdfId, linkedCell, rectangle)
                    {
                        LinkType = LinkType.Table,
                        TableGrid = CloneGrid(target.Grid),
                    };

                    LinkCellTracker.BindCell(workbook, targetAnchor, linkedCell.TrackIndex);
                    try
                    {
                        writer.WriteCreate(targetAnchor, copiedLink.TableGrid, target.Cells);
                        CellFormattingService.ApplyLinkStyle(targetAnchor, LinkType.Table);
                    }
                    catch
                    {
                        LinkCellTracker.UnbindCell(workbook, targetAnchor, linkedCell.TrackIndex);
                        throw;
                    }

                    // Preserve the copy flow's existing behavior of following each table,
                    // even when its anchor was already inside the visible window.
                    ExcelCellNavigationService.BringIntoView(targetAnchor, alignToTopLeft: true);
                    copiedLinks.Add(copiedLink);
                    onCopied?.Invoke(copiedLink);
                    rowOffset += target.Grid.RowCount;
                }
            }
            catch
            {
                // Keep successfully imported pages recoverable if a later page fails.
                session.AddLinks(copiedLinks);
                throw;
            }

            // Persist the complete batch once instead of replacing the Custom XML part for
            // every target page. The progress callback above remains page-by-page.
            session.AddLinks(copiedLinks);
            return session.GetLinks();
        }

        private static TableGrid CloneGrid(TableGrid source)
        {
            return new TableGrid
            {
                ColumnBoundaries = source.ColumnBoundaries.ToList(),
                RowBoundaries = source.RowBoundaries.ToList(),
            };
        }

        private static TableGrid MergeGrid(TableGrid source, TableGrid target)
        {
            return new TableGrid
            {
                ColumnBoundaries = source.ColumnBoundaries.ToList(),
                RowBoundaries = target.RowBoundaries.ToList(),
            };
        }
    }
}
