using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using DocuLink.Addin.Modules.CustomXml.Models;

namespace DocuLink.Addin.Modules.CustomXml.Serialization
{
    public static class DocuLinkStorageSerializer
    {
        private const string VersionAttribute = "version";

        private const string IdAttribute = "id";

        private const string Base64Attribute = "Base64";

        private const string SheetAttribute = "sheet";

        private const string AddressAttribute = "address";

        private const string PageAttribute = "page";

        private const string XAttribute = "x";

        private const string YAttribute = "y";

        private const string WidthAttribute = "width";

        private const string HeightAttribute = "height";

        private const string CoordinateSpaceAttribute = "coordinateSpace";

        public static DocuLinkStorage FromXDocument(XDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            XElement root = document.Root;
            if (root == null)
                throw new InvalidOperationException("DocuLink storage XML has no root element.");

            if (root.Name != DocuLinkXml.Ns + DocuLinkXml.RootElementName)
                throw new InvalidOperationException(
                    "Root element must be {" + DocuLinkXml.NamespaceUri + "}" + DocuLinkXml.RootElementName + ".");

            XAttribute versionAttribute = root.Attribute(VersionAttribute);
            if (versionAttribute == null)
                throw new InvalidOperationException("DocuLink storage root is missing required attribute 'version'.");

            if (!uint.TryParse(versionAttribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint fileVersion)
                || fileVersion != DocuLinkXml.SchemaVersion)
                throw new InvalidOperationException(
                    "Unsupported DocuLink storage version; expected " + DocuLinkXml.SchemaVersion + ".");

            XElement linksElement = root.Element(DocuLinkXml.Ns + DocuLinkXml.LinksElementName);
            if (linksElement == null)
                throw new InvalidOperationException(
                    "DocuLink storage is missing required element '" + DocuLinkXml.LinksElementName + "'.");

            var links = new List<DocumentLink>(
                linksElement
                    .Elements(DocuLinkXml.Ns + DocuLinkXml.LinkElementName)
                    .Select((element, idx) => ParseLink(element, idx)));

            return new DocuLinkStorage(fileVersion, links);
        }

        public static XDocument ToXDocument(DocuLinkStorage storage)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            if (storage.Version != DocuLinkXml.SchemaVersion)
                throw new InvalidOperationException(
                    "Unsupported storage version for serialization; expected " + DocuLinkXml.SchemaVersion + ".");

            var linksElement = new XElement(
                DocuLinkXml.Ns + DocuLinkXml.LinksElementName,
                storage.Links.Select((link, i) => SerializeLink(link, i)));

            var root = new XElement(
                DocuLinkXml.Ns + DocuLinkXml.RootElementName,
                new XAttribute(VersionAttribute, DocuLinkXml.SchemaVersion),
                linksElement);

            var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            return document;
        }

        private static DocumentLink ParseLink(XElement linkElement, int index)
        {
            XAttribute idAttribute = linkElement.Attribute(IdAttribute);
            if (idAttribute == null || string.IsNullOrWhiteSpace(idAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + index + " is missing required attribute 'id'.");

            XElement pdfElement = linkElement.Element(DocuLinkXml.Ns + DocuLinkXml.PdfElementName);
            if (pdfElement == null)
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + index + " is missing required element '" + DocuLinkXml.PdfElementName + "'.");

            XAttribute base64Attribute = pdfElement.Attribute(Base64Attribute);
            if (base64Attribute == null)
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + index + " Pdf is missing required attribute 'Base64'.");

            XElement cellElement = linkElement.Element(DocuLinkXml.Ns + DocuLinkXml.CellElementName);
            if (cellElement == null)
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + index + " is missing required element '" + DocuLinkXml.CellElementName + "'.");

            XAttribute sheetAttribute = cellElement.Attribute(SheetAttribute);
            XAttribute addressAttribute = cellElement.Attribute(AddressAttribute);
            if (sheetAttribute == null || string.IsNullOrWhiteSpace(sheetAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + index + " Cell is missing required attribute 'sheet'.");
            if (addressAttribute == null || string.IsNullOrWhiteSpace(addressAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + index + " Cell is missing required attribute 'address'.");

            XElement rectElement = linkElement.Element(DocuLinkXml.Ns + DocuLinkXml.RectElementName);
            if (rectElement == null)
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + index + " is missing required element '" + DocuLinkXml.RectElementName + "'.");

            XAttribute pageAttr = rectElement.Attribute(PageAttribute);
            if (pageAttr == null
                || !uint.TryParse(pageAttr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pageValue))
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + index + " Rect is missing a valid non-negative 'page' attribute.");

            if (pageValue > int.MaxValue)
                throw new InvalidOperationException("DocuLink storage Link #" + index + " page index is too large.");

            RectangleCoordinateSpace space = ParseCoordinateSpace(rectElement, index);
            double x = ReadRequiredDouble(rectElement, XAttribute, index);
            double y = ReadRequiredDouble(rectElement, YAttribute, index);
            double w = ReadRequiredDouble(rectElement, WidthAttribute, index);
            double h = ReadRequiredDouble(rectElement, HeightAttribute, index);

            var rect = new PdfRectangle(
                (int)pageValue,
                x,
                y,
                w,
                h,
                space);

            var cell = new LinkedCellRef(sheetAttribute.Value, addressAttribute.Value);

            return new DocumentLink(idAttribute.Value.Trim(), base64Attribute.Value ?? string.Empty, cell, rect);
        }

        private static RectangleCoordinateSpace ParseCoordinateSpace(XElement rectElement, int index)
        {
            XAttribute spaceAttr = rectElement.Attribute(CoordinateSpaceAttribute);
            if (spaceAttr == null || string.IsNullOrWhiteSpace(spaceAttr.Value))
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + index + " Rect is missing required attribute 'coordinateSpace'.");

            string value = spaceAttr.Value.Trim();
            if (string.Equals(value, "normalized", StringComparison.OrdinalIgnoreCase))
                return RectangleCoordinateSpace.Normalized;

            throw new InvalidOperationException(
                "DocuLink storage Link #" + index + " has unsupported coordinateSpace '" + value + "'.");
        }

        private static double ReadRequiredDouble(XElement element, string attributeName, int linkIndex)
        {
            XAttribute attribute = element.Attribute(attributeName);
            if (attribute == null)
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + linkIndex + " Rect is missing required attribute '" + attributeName + "'.");

            if (!double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new InvalidOperationException(
                    "DocuLink storage Link #" + linkIndex + " Rect attribute '" + attributeName + "' is not a valid number.");

            return value;
        }

        private static XElement SerializeLink(DocumentLink link, int index)
        {
            if (link == null) throw new ArgumentNullException(nameof(link));
            if (string.IsNullOrWhiteSpace(link.Id))
                throw new InvalidOperationException("DocumentLink at index " + index + " has an empty Id.");
            if (link.LinkedCell == null) throw new InvalidOperationException("DocumentLink '" + link.Id + "' has no LinkedCell.");
            if (link.Rectangle == null) throw new InvalidOperationException("DocumentLink '" + link.Id + "' has no Rectangle.");

            return new XElement(
                DocuLinkXml.Ns + DocuLinkXml.LinkElementName,
                new XAttribute(IdAttribute, link.Id),
                new XElement(
                    DocuLinkXml.Ns + DocuLinkXml.PdfElementName,
                    new XAttribute(Base64Attribute, link.PdfBase64 ?? string.Empty)),
                new XElement(
                    DocuLinkXml.Ns + DocuLinkXml.CellElementName,
                    new XAttribute(SheetAttribute, link.LinkedCell.SheetName ?? string.Empty),
                    new XAttribute(AddressAttribute, link.LinkedCell.Address ?? string.Empty)),
                SerializeRect(link.Rectangle, link.Id));
        }

        private static XElement SerializeRect(PdfRectangle rect, string linkId)
        {
            if (rect.PageIndex < 0)
                throw new InvalidOperationException("Rectangle for link '" + linkId + "' has a negative page index.");

            if (rect.CoordinateSpace != RectangleCoordinateSpace.Normalized)
                throw new InvalidOperationException(
                    "Rectangle for link '" + linkId + "' uses a coordinate space that is not supported for serialization.");

            return new XElement(
                DocuLinkXml.Ns + DocuLinkXml.RectElementName,
                new XAttribute(PageAttribute, (uint)rect.PageIndex),
                new XAttribute(XAttribute, rect.X.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(YAttribute, rect.Y.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(WidthAttribute, rect.Width.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(HeightAttribute, rect.Height.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(CoordinateSpaceAttribute, "normalized"));
        }
    }
}
