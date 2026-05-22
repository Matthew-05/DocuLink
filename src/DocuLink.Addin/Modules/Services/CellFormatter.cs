using System.Collections.Generic;
using System.Drawing;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>Applies visual styles to Excel cells that host linked rectangles.</summary>
    internal static class CellFormatter
    {
        private static readonly int LightBlue = ColorTranslator.ToOle(Color.LightBlue);

        /// <summary>Applies the standard link-rectangle cell style (light blue fill).</summary>
        public static void ApplyLinkStyle(Excel.Range cell)
        {
            cell.Interior.Color = LightBlue;
        }

        /// <summary>Removes the link-rectangle cell background fill.</summary>
        public static void ClearLinkStyle(Excel.Range cell)
        {
            cell.Interior.Pattern = Excel.XlPattern.xlPatternNone;
        }

        /// <summary>
        /// Clears link background fill on all <paramref name="cells"/> in one pass
        /// per worksheet by unioning ranges, so Excel repaints once per sheet.
        /// </summary>
        public static void ClearLinkStyles(IEnumerable<Excel.Range> cells, Excel.Application app)
        {
            if (app == null) return;

            var bySheet = new Dictionary<Excel.Worksheet, List<Excel.Range>>();

            foreach (Excel.Range cell in cells)
            {
                if (cell == null) continue;

                var ws = (Excel.Worksheet)cell.Worksheet;
                if (!bySheet.TryGetValue(ws, out List<Excel.Range> list))
                {
                    list = new List<Excel.Range>();
                    bySheet[ws] = list;
                }

                list.Add(cell);
            }

            foreach (List<Excel.Range> sheetCells in bySheet.Values)
            {
                if (sheetCells.Count == 0) continue;

                Excel.Range batch = sheetCells[0];
                for (int i = 1; i < sheetCells.Count; i++)
                    batch = app.Union(batch, sheetCells[i]);

                batch.Interior.Pattern = Excel.XlPattern.xlPatternNone;
            }
        }
    }
}