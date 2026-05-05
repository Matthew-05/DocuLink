namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class LinkedCell
    {
        public LinkedCell(string sheetName, string address)
        {
            SheetName = sheetName;
            Address = address;
        }

        public string SheetName { get; set; }

        public string Address { get; set; }
    }
}
