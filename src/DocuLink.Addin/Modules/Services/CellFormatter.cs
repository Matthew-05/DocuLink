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
    }
}
