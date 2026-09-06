using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Talliark.Addin.Modules.CustomXml.Models;
using Talliark.Addin.Modules.Services;

namespace Talliark.Addin.Modules.CustomXml.Serialization
{
    public static class TalliarkContentSerializer
    {
        private const string VersionAttribute = "version";
        private const string IdAttribute = "id";
        private const string NameAttribute = "name";

        public static TalliarkContent FromXDocument(XDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            XElement root = document.Root;
            if (root == null)
                throw new InvalidOperationException("Talliark content XML has no root element.");

            if (root.Name != TalliarkXml.ContentNs + TalliarkXml.ContentRootElementName)
                throw new InvalidOperationException(
                    "Root element must be {" + TalliarkXml.ContentNamespaceUri + "}" + TalliarkXml.ContentRootElementName + ".");

            uint fileVersion = ReadVersion(root);

            XElement foldersElement = root.Element(TalliarkXml.ContentNs + TalliarkXml.FoldersElementName);
            var folders = foldersElement != null
                ? new List<PdfFolder>(foldersElement
                    .Elements(TalliarkXml.ContentNs + TalliarkXml.FolderElementName)
                    .Select((el, i) => ParseFolder(el, i)))
                : new List<PdfFolder>();

            XElement pdfsElement = root.Element(TalliarkXml.ContentNs + TalliarkXml.PdfsElementName);
            if (pdfsElement == null)
                throw new InvalidOperationException(
                    "Talliark content is missing required element '" + TalliarkXml.PdfsElementName + "'.");

            var pdfs = new List<PdfMetadata>(
                pdfsElement
                    .Elements(TalliarkXml.ContentNs + TalliarkXml.PdfElementName)
                    .Select((el, i) => ParsePdf(el, i)));

            return new TalliarkContent(fileVersion, folders, pdfs);
        }

        public static XDocument ToXDocument(TalliarkContent content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));

            if (content.Version != TalliarkXml.SchemaVersion)
                throw new InvalidOperationException(
                    "Unsupported content version for serialization; expected " + TalliarkXml.SchemaVersion + ".");

            var foldersElement = new XElement(
                TalliarkXml.ContentNs + TalliarkXml.FoldersElementName,
                content.Folders.Select((folder, i) => SerializeFolder(folder, i)));

            var pdfsElement = new XElement(
                TalliarkXml.ContentNs + TalliarkXml.PdfsElementName,
                content.Pdfs.Select((pdf, i) => SerializePdf(pdf, i)));

            var root = new XElement(
                TalliarkXml.ContentNs + TalliarkXml.ContentRootElementName,
                new XAttribute(VersionAttribute, TalliarkXml.SchemaVersion),
                foldersElement,
                pdfsElement);

            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        private static uint ReadVersion(XElement root)
        {
            XAttribute versionAttribute = root.Attribute(VersionAttribute);
            if (versionAttribute == null)
                throw new InvalidOperationException("Talliark content root is missing required attribute 'version'.");

            if (!uint.TryParse(versionAttribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint fileVersion)
                || fileVersion != TalliarkXml.SchemaVersion)
                throw new InvalidOperationException(
                    "Unsupported Talliark content version; expected " + TalliarkXml.SchemaVersion + ".");

            return fileVersion;
        }

        private static PdfFolder ParseFolder(XElement element, int index)
        {
            XAttribute idAttr = element.Attribute(IdAttribute);
            if (idAttr == null || string.IsNullOrWhiteSpace(idAttr.Value))
                throw new InvalidOperationException("Talliark content Folder #" + index + " is missing required attribute 'id'.");

            return new PdfFolder(idAttr.Value.Trim(), element.Attribute(NameAttribute)?.Value ?? string.Empty);
        }

        private static XElement SerializeFolder(PdfFolder folder, int index)
        {
            if (folder == null) throw new ArgumentNullException(nameof(folder));
            if (string.IsNullOrWhiteSpace(folder.Id))
                throw new InvalidOperationException("PdfFolder at index " + index + " has an empty Id.");

            return new XElement(
                TalliarkXml.ContentNs + TalliarkXml.FolderElementName,
                new XAttribute(IdAttribute, folder.Id),
                new XAttribute(NameAttribute, folder.Name ?? string.Empty));
        }

        private static PdfMetadata ParsePdf(XElement pdfElement, int index)
        {
            XAttribute idAttribute = pdfElement.Attribute(IdAttribute);
            if (idAttribute == null || string.IsNullOrWhiteSpace(idAttribute.Value))
                throw new InvalidOperationException(
                    "Talliark content Pdf #" + index + " is missing required attribute 'id'.");

            string name = pdfElement.Attribute(NameAttribute)?.Value ?? string.Empty;

            string folderId = pdfElement.Attribute(TalliarkXml.FolderIdAttribute)?.Value;
            if (string.IsNullOrWhiteSpace(folderId)) folderId = null;

            DateTime? dateAdded = null;
            string dateAddedStr = pdfElement.Attribute(TalliarkXml.DateAddedAttribute)?.Value;
            if (!string.IsNullOrWhiteSpace(dateAddedStr)
                && DateTime.TryParse(dateAddedStr, null, DateTimeStyles.RoundtripKind, out DateTime parsedDate))
                dateAdded = parsedDate;

            long fileSizeBytes = 0;
            string fileSizeStr = pdfElement.Attribute(TalliarkXml.FileSizeBytesAttribute)?.Value;
            if (!string.IsNullOrWhiteSpace(fileSizeStr))
                long.TryParse(fileSizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileSizeBytes);

            string ocrStatus = pdfElement.Attribute(TalliarkXml.OcrStatusAttribute)?.Value ?? PdfStatus.None;

            Dictionary<int, int> pageRotations = null;
            XElement rotationsEl = pdfElement.Element(TalliarkXml.ContentNs + TalliarkXml.PageRotationsElementName);
            if (rotationsEl != null)
            {
                foreach (XElement pageEl in rotationsEl.Elements(TalliarkXml.ContentNs + TalliarkXml.PageRotationElementName))
                {
                    string indexStr    = pageEl.Attribute(TalliarkXml.PageIndexAttribute)?.Value;
                    string rotationStr = pageEl.Attribute(TalliarkXml.RotationAttribute)?.Value;
                    if (int.TryParse(indexStr, out int pageIdx) && int.TryParse(rotationStr, out int rot) && rot != 0)
                    {
                        if (pageRotations == null) pageRotations = new Dictionary<int, int>();
                        pageRotations[pageIdx] = rot;
                    }
                }
            }

            return new PdfMetadata(idAttribute.Value.Trim(), name, folderId, dateAdded, fileSizeBytes)
            {
                OcrStatus     = ocrStatus,
                PageRotations = pageRotations,
            };
        }

        private static XElement SerializePdf(PdfMetadata pdf, int index)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));
            if (string.IsNullOrWhiteSpace(pdf.Id))
                throw new InvalidOperationException("PdfMetadata at index " + index + " has an empty Id.");

            var element = new XElement(
                TalliarkXml.ContentNs + TalliarkXml.PdfElementName,
                new XAttribute(IdAttribute, pdf.Id),
                new XAttribute(NameAttribute, pdf.Name ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(pdf.FolderId))
                element.Add(new XAttribute(TalliarkXml.FolderIdAttribute, pdf.FolderId));

            if (pdf.DateAdded.HasValue)
                element.Add(new XAttribute(TalliarkXml.DateAddedAttribute,
                    pdf.DateAdded.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));

            if (pdf.FileSizeBytes > 0)
                element.Add(new XAttribute(TalliarkXml.FileSizeBytesAttribute,
                    pdf.FileSizeBytes.ToString(CultureInfo.InvariantCulture)));

            if (!string.IsNullOrWhiteSpace(pdf.OcrStatus) && pdf.OcrStatus != PdfStatus.None)
                element.Add(new XAttribute(TalliarkXml.OcrStatusAttribute, pdf.OcrStatus));

            if (pdf.PageRotations != null && pdf.PageRotations.Count > 0)
            {
                var rotationsElement = new XElement(TalliarkXml.ContentNs + TalliarkXml.PageRotationsElementName);
                foreach (var kvp in pdf.PageRotations)
                {
                    if (kvp.Value == 0) continue;
                    rotationsElement.Add(new XElement(
                        TalliarkXml.ContentNs + TalliarkXml.PageRotationElementName,
                        new XAttribute(TalliarkXml.PageIndexAttribute, kvp.Key),
                        new XAttribute(TalliarkXml.RotationAttribute,  kvp.Value)));
                }
                if (rotationsElement.HasElements)
                    element.Add(rotationsElement);
            }

            return element;
        }
    }
}
