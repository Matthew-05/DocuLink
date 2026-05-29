using System;
using System.Collections.Generic;

namespace DocuLink.Addin.Modules.CustomXml.Models
{
    /// <summary>
    /// Lightweight metadata for an embedded PDF — no binary data.
    /// Persisted in the tiny content Custom XML part; binary lives in per-PDF binary parts.
    /// </summary>
    public sealed class PdfMetadata
    {
        public PdfMetadata(string id, string name, string folderId = null,
            DateTime? dateAdded = null, long fileSizeBytes = 0)
        {
            Id = id;
            Name = name;
            FolderId = folderId;
            DateAdded = dateAdded;
            FileSizeBytes = fileSizeBytes;
        }

        public string Id { get; set; }

        public string Name { get; set; }

        public string FolderId { get; set; }

        public DateTime? DateAdded { get; set; }

        public long FileSizeBytes { get; set; }

        public string OcrStatus { get; set; } = "none";

        /// <summary>
        /// Per-page clockwise rotation in degrees (0, 90, 180, 270).
        /// Only non-zero pages are stored. Null or empty means all pages at 0°.
        /// </summary>
        public Dictionary<int, int> PageRotations { get; set; }
    }
}
