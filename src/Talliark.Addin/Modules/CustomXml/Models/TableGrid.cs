using System.Collections.Generic;

namespace Talliark.Addin.Modules.CustomXml.Models
{
    /// <summary>
    /// Internal table boundaries stored as fractions of the owning rectangle's dimensions.
    /// The rectangle edges are implicit boundaries at zero and one.
    /// </summary>
    public sealed class TableGrid
    {
        public TableGrid()
        {
            ColumnBoundaries = new List<double>();
            RowBoundaries = new List<double>();
        }

        public IList<double> ColumnBoundaries { get; set; }

        public IList<double> RowBoundaries { get; set; }

        public int ColumnCount => (ColumnBoundaries?.Count ?? 0) + 1;

        public int RowCount => (RowBoundaries?.Count ?? 0) + 1;
    }
}
