namespace DocuLink.Addin.Modules.CustomXml.Models
{
    /// <summary>
    /// Payloads stored together in a per-PDF Custom XML part. Keeping the cache
    /// artifacts named prevents positional string parameters from being swapped as
    /// new document-analysis stages are added.
    /// </summary>
    public sealed class PdfBinaryParts
    {
        public string Base64 { get; set; } = string.Empty;

        public string GeometryBase64 { get; set; }

        public string TableStructureBase64 { get; set; }

        public string DocumentValuesBase64 { get; set; }

        public string FsStructureBase64 { get; set; }
    }
}
