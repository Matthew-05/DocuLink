using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using DocuLink.Addin.Modules.CustomXml.Models;
using DocuLink.Addin.Modules.Services;

namespace DocuLink.Addin.Modules.CustomXml.Serialization
{
    public static class DocuLinkContentSerializer
    {
        private const string VersionAttribute = "version";
        private const string IdAttribute = "id";
        private const string NameAttribute = "name";

        public static DocuLinkContent FromXDocument(XDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            XElement root = document.Root;
            if (root == null)
                throw new InvalidOperationException("DocuLink content XML has no root element.");

            if (root.Name != DocuLinkXml.ContentNs + DocuLinkXml.ContentRootElementName)
                throw new InvalidOperationException(
                    "Root element must be {" + DocuLinkXml.ContentNamespaceUri + "}" + DocuLinkXml.ContentRootElementName + ".");

            uint fileVersion = ReadVersion(root);

            XElement foldersElement = root.Element(DocuLinkXml.ContentNs + DocuLinkXml.FoldersElementName);
            var folders = foldersElement != null
                ? new List<PdfFolder>(foldersElement
                    .Elements(DocuLinkXml.ContentNs + DocuLinkXml.FolderElementName)
                    .Select((el, i) => ParseFolder(el, i)))
                : new List<PdfFolder>();

            XElement pdfsElement = root.Element(DocuLinkXml.ContentNs + DocuLinkXml.PdfsElementName);
            if (pdfsElement == null)
                throw new InvalidOperationException(
                    "DocuLink content is missing required element '" + DocuLinkXml.PdfsElementName + "'.");

            var pdfs = new List<PdfMetadata>(
                pdfsElement
                    .Elements(DocuLinkXml.ContentNs + DocuLinkXml.PdfElementName)
                    .Select((el, i) => ParsePdf(el, i)));

            return new DocuLinkContent(fileVersion, folders, pdfs);
        }

        public static XDocument ToXDocument(DocuLinkContent content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));

            if (content.Version != DocuLinkXml.SchemaVersion)
                throw new InvalidOperationException(
                    "Unsupported content version for serialization; expected " + DocuLinkXml.SchemaVersion + ".");

            var foldersElement = new XElement(
                DocuLinkXml.ContentNs + DocuLinkXml.FoldersElementName,
                content.Folders.Select((folder, i) => SerializeFolder(folder, i)));

            var pdfsElement = new XElement(
                DocuLinkXml.ContentNs + DocuLinkXml.PdfsElementName,
                content.Pdfs.Select((pdf, i) => SerializePdf(pdf, i)));

            var root = new XElement(
                DocuLinkXml.ContentNs + DocuLinkXml.ContentRootElementName,
                new XAttribute(VersionAttribute, DocuLinkXml.SchemaVersion),
                foldersElement,
                pdfsElement);

            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        private static uint ReadVersion(XElement root)
        {
            XAttribute versionAttribute = root.Attribute(VersionAttribute);
            if (versionAttribute == null)
                throw new InvalidOperationException("DocuLink content root is missing required attribute 'version'.");

            if (!uint.TryParse(versionAttribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint fileVersion)
                || fileVersion != DocuLinkXml.SchemaVersion)
                throw new InvalidOperationException(
                    "Unsupported DocuLink content version; expected " + DocuLinkXml.SchemaVersion + ".");

            return fileVersion;
        }

        private static PdfFolder ParseFolder(XElement element, int index)
        {
            XAttribute idAttr = element.Attribute(IdAttribute);
            if (idAttr == null || string.IsNullOrWhiteSpace(idAttr.Value))
                throw new InvalidOperationException("DocuLink content Folder #" + index + " is missing required attribute 'id'.");

            return new PdfFolder(idAttr.Value.Trim(), element.Attribute(NameAttribute)?.Value ?? string.Empty);
        }

        private static XElement SerializeFolder(PdfFolder folder, int index)
        {
            if (folder == null) throw new ArgumentNullException(nameof(folder));
            if (string.IsNullOrWhiteSpace(folder.Id))
                throw new InvalidOperationException("PdfFolder at index " + index + " has an empty Id.");

            return new XElement(
                DocuLinkXml.ContentNs + DocuLinkXml.FolderElementName,
                new XAttribute(IdAttribute, folder.Id),
                new XAttribute(NameAttribute, folder.Name ?? string.Empty));
        }

        private static PdfMetadata ParsePdf(XElement pdfElement, int index)
        {
            XAttribute idAttribute = pdfElement.Attribute(IdAttribute);
            if (idAttribute == null || string.IsNullOrWhiteSpace(idAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink content Pdf #" + index + " is missing required attribute 'id'.");

            string name = pdfElement.Attribute(NameAttribute)?.Value ?? string.Empty;

            string folderId = pdfElement.Attribute(DocuLinkXml.FolderIdAttribute)?.Value;
            if (string.IsNullOrWhiteSpace(folderId)) folderId = null;

            DateTime? dateAdded = null;
            string dateAddedStr = pdfElement.Attribute(DocuLinkXml.DateAddedAttribute)?.Value;
            if (!string.IsNullOrWhiteSpace(dateAddedStr)
                && DateTime.TryParse(dateAddedStr, null, DateTimeStyles.RoundtripKind, out DateTime parsedDate))
                dateAdded = parsedDate;

            long fileSizeBytes = 0;
            string fileSizeStr = pdfElement.Attribute(DocuLinkXml.FileSizeBytesAttribute)?.Value;
            if (!string.IsNullOrWhiteSpace(fileSizeStr))
                long.TryParse(fileSizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileSizeBytes);

            string ocrStatus = pdfElement.Attribute(DocuLinkXml.OcrStatusAttribute)?.Value ?? PdfStatus.None;

            return new PdfMetadata(idAttribute.Value.Trim(), name, folderId, dateAdded, fileSizeBytes)
            {
                OcrStatus = ocrStatus,
            };
        }

        private static XElement SerializePdf(PdfMetadata pdf, int index)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));
            if (string.IsNullOrWhiteSpace(pdf.Id))
                throw new InvalidOperationException("PdfMetadata at index " + index + " has an empty Id.");

            var element = new XElement(
                DocuLinkXml.ContentNs + DocuLinkXml.PdfElementName,
                new XAttribute(IdAttribute, pdf.Id),
                new XAttribute(NameAttribute, pdf.Name ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(pdf.FolderId))
                element.Add(new XAttribute(DocuLinkXml.FolderIdAttribute, pdf.FolderId));

            if (pdf.DateAdded.HasValue)
                element.Add(new XAttribute(DocuLinkXml.DateAddedAttribute,
                    pdf.DateAdded.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));

            if (pdf.FileSizeBytes > 0)
                element.Add(new XAttribute(DocuLinkXml.FileSizeBytesAttribute,
                    pdf.FileSizeBytes.ToString(CultureInfo.InvariantCulture)));

            if (!string.IsNullOrWhiteSpace(pdf.OcrStatus) && pdf.OcrStatus != PdfStatus.None)
                element.Add(new XAttribute(DocuLinkXml.OcrStatusAttribute, pdf.OcrStatus));

            return element;
        }
    }
}
