using System;

namespace DocuLink.Addin.Modules.CustomXml.Models
{
    public sealed class PdfDocument
    {
        public PdfDocument(string id, string name, string base64,
            string folderId = null, DateTime? dateAdded = null, long fileSizeBytes = 0)
        {
            Id = id;
            Name = name;
            Base64 = base64;
            FolderId = folderId;
            DateAdded = dateAdded;
            FileSizeBytes = fileSizeBytes;
        }

        public string Id { get; set; }

        public string Name { get; set; }

        public string Base64 { get; set; }

        /// <summary>GUID of the owning folder, or null if uncategorised.</summary>
        public string FolderId { get; set; }

        /// <summary>UTC datetime when the file was first added to the workbook.</summary>
        public DateTime? DateAdded { get; set; }

        /// <summary>Original file size in bytes.</summary>
        public long FileSizeBytes { get; set; }
    }
}
