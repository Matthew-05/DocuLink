namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class PdfRectangle
    {
        public PdfRectangle(int pageIndex, double x, double y, double width, double height, RectangleCoordinateSpace coordinateSpace)
        {
            PageIndex = pageIndex;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            CoordinateSpace = coordinateSpace;
        }

        public int PageIndex { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public RectangleCoordinateSpace CoordinateSpace { get; set; }
    }
}
