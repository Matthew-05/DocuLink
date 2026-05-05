namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class PdfDocument
    {
        public PdfDocument(string id, string base64)
        {
            Id = id;
            Base64 = base64;
        }

        public string Id { get; set; }

        public string Base64 { get; set; }
    }
}
