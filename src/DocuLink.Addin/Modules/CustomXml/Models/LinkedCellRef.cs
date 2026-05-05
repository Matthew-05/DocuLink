namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class LinkedCellRef
    {
        public LinkedCellRef(string sheetName, string address)
        {
            SheetName = sheetName;
            Address = address;
        }

        public string SheetName { get; set; }

        public string Address { get; set; }
    }
}
