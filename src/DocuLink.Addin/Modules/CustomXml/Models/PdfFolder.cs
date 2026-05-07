namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class PdfFolder
    {
        public PdfFolder(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; set; }

        public string Name { get; set; }
    }
}
