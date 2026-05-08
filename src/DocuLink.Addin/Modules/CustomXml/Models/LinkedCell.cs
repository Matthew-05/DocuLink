namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class LinkedCell
    {
        public LinkedCell(string sheetName, string address, int trackIndex)
        {
            SheetName = sheetName;
            Address = address;
            TrackIndex = trackIndex;
        }

        public string SheetName { get; set; }

        public string Address { get; set; }

        /// <summary>
        /// Stable, monotonically-increasing integer assigned at link creation.
        /// Maps to the XmlMap XPath "/DocuLinkCellTrack/Link[n]" used for
        /// position tracking. Never reused after a link is deleted.
        /// </summary>
        public int TrackIndex { get; set; }
    }
}
