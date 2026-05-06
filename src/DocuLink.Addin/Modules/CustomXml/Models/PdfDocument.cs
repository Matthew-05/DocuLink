namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class PdfDocument
    {
        public PdfDocument(string id, string name, string base64)
        {
            Id = id;
            Name = name;
            Base64 = base64;
        }

        public string Id { get; set; }

        public string Name { get; set; }

        public string Base64 { get; set; }
    }
}
