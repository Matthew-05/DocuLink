using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using DocuLink.Addin.Modules.CustomXml.Models;

namespace DocuLink.Addin.Modules.CustomXml.Serialization
{
    public static class DocuLinkLinksSerializer
    {
        private const string VersionAttribute = "version";
        private const string IdAttribute = "id";
        private const string SheetAttribute = "sheet";
        private const string AddressAttribute = "address";
        private const string TrackIndexAttribute = "trackIndex";
        private const string PageAttribute = "page";
        private const string XAttribute = "x";
        private const string YAttribute = "y";
        private const string WidthAttribute = "width";
        private const string HeightAttribute = "height";
        private const string CoordinateSpaceAttribute = "coordinateSpace";
        private const string LinkTypeAttribute = "linkType";
        private const string SourceTextAttribute = "sourceText";

        public static IList<LinkedRectangle> FromXDocument(XDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            XElement root = document.Root;
            if (root == null)
                throw new InvalidOperationException("DocuLink links XML has no root element.");

            if (root.Name != DocuLinkXml.LinksNs + DocuLinkXml.LinksRootElementName)
                throw new InvalidOperationException(
                    "Root element must be {" + DocuLinkXml.LinksNamespaceUri + "}" + DocuLinkXml.LinksRootElementName + ".");

            ReadVersion(root);

            XElement linkedRectanglesElement = root.Element(DocuLinkXml.LinksNs + DocuLinkXml.LinkedRectanglesElementName);
            if (linkedRectanglesElement == null)
                throw new InvalidOperationException(
                    "DocuLink links is missing required element '" + DocuLinkXml.LinkedRectanglesElementName + "'.");

            return new List<LinkedRectangle>(
                linkedRectanglesElement
                    .Elements(DocuLinkXml.LinksNs + DocuLinkXml.LinkedRectangleElementName)
                    .Select((element, idx) => ParseLinkedRectangle(element, idx)));
        }

        public static XDocument ToXDocument(IEnumerable<LinkedRectangle> linkedRectangles)
        {
            var rects = linkedRectangles != null
                ? linkedRectangles.ToList()
                : new List<LinkedRectangle>();

            var linkedRectanglesElement = new XElement(
                DocuLinkXml.LinksNs + DocuLinkXml.LinkedRectanglesElementName,
                rects.Select((rect, i) => SerializeLinkedRectangle(rect, i)));

            var root = new XElement(
                DocuLinkXml.LinksNs + DocuLinkXml.LinksRootElementName,
                new XAttribute(VersionAttribute, DocuLinkXml.SchemaVersion),
                linkedRectanglesElement);

            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        private static void ReadVersion(XElement root)
        {
            XAttribute versionAttribute = root.Attribute(VersionAttribute);
            if (versionAttribute == null)
                throw new InvalidOperationException("DocuLink links root is missing required attribute 'version'.");

            if (!uint.TryParse(versionAttribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint fileVersion)
                || fileVersion != DocuLinkXml.SchemaVersion)
                throw new InvalidOperationException(
                    "Unsupported DocuLink links version; expected " + DocuLinkXml.SchemaVersion + ".");
        }

        private static LinkedRectangle ParseLinkedRectangle(XElement element, int index)
        {
            XAttribute idAttribute = element.Attribute(IdAttribute);
            if (idAttribute == null || string.IsNullOrWhiteSpace(idAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " is missing required attribute 'id'.");

            XAttribute pdfIdAttribute = element.Attribute(DocuLinkXml.PdfIdAttribute);
            if (pdfIdAttribute == null || string.IsNullOrWhiteSpace(pdfIdAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " is missing required attribute 'pdfId'.");

            XElement cellElement = element.Element(DocuLinkXml.LinksNs + DocuLinkXml.CellElementName);
            if (cellElement == null)
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " is missing required element '" + DocuLinkXml.CellElementName + "'.");

            XAttribute sheetAttribute = cellElement.Attribute(SheetAttribute);
            XAttribute addressAttribute = cellElement.Attribute(AddressAttribute);
            XAttribute trackIndexAttribute = cellElement.Attribute(TrackIndexAttribute);
            if (sheetAttribute == null || string.IsNullOrWhiteSpace(sheetAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " Cell is missing required attribute 'sheet'.");
            if (addressAttribute == null || string.IsNullOrWhiteSpace(addressAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " Cell is missing required attribute 'address'.");
            if (trackIndexAttribute == null
                || !int.TryParse(trackIndexAttribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int trackIndexValue)
                || trackIndexValue <= 0)
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " Cell is missing a valid positive 'trackIndex' attribute.");

            XElement rectElement = element.Element(DocuLinkXml.LinksNs + DocuLinkXml.RectElementName);
            if (rectElement == null)
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " is missing required element '" + DocuLinkXml.RectElementName + "'.");

            XAttribute pageAttr = rectElement.Attribute(PageAttribute);
            if (pageAttr == null
                || !uint.TryParse(pageAttr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pageValue))
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " Rect is missing a valid non-negative 'page' attribute.");

            if (pageValue > int.MaxValue)
                throw new InvalidOperationException("DocuLink links LinkedRectangle #" + index + " page index is too large.");

            RectangleCoordinateSpace space = ParseCoordinateSpace(rectElement, index);
            double x = ReadRequiredDouble(rectElement, XAttribute, index);
            double y = ReadRequiredDouble(rectElement, YAttribute, index);
            double w = ReadRequiredDouble(rectElement, WidthAttribute, index);
            double h = ReadRequiredDouble(rectElement, HeightAttribute, index);

            var rect = new PdfRectangle((int)pageValue, x, y, w, h, space);
            var cell = new LinkedCell(sheetAttribute.Value, addressAttribute.Value, trackIndexValue);

            LinkType linkType = ParseLinkType(element);
            string sourceText = element.Attribute(SourceTextAttribute)?.Value;

            return new LinkedRectangle(idAttribute.Value.Trim(), pdfIdAttribute.Value.Trim(), cell, rect)
            {
                LinkType   = linkType,
                SourceText = string.IsNullOrEmpty(sourceText) ? null : sourceText,
            };
        }

        private static LinkType ParseLinkType(XElement element)
        {
            string value = element.Attribute(LinkTypeAttribute)?.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return LinkType.Auto;
            if (string.Equals(value, "raw",  StringComparison.OrdinalIgnoreCase)) return LinkType.Raw;
            if (string.Equals(value, "sum",  StringComparison.OrdinalIgnoreCase)) return LinkType.Sum;
            return LinkType.Auto;
        }

        private static string SerializeLinkType(LinkType linkType)
        {
            switch (linkType)
            {
                case LinkType.Raw: return "raw";
                case LinkType.Sum: return "sum";
                default:           return "auto";
            }
        }

        private static RectangleCoordinateSpace ParseCoordinateSpace(XElement rectElement, int index)
        {
            XAttribute spaceAttr = rectElement.Attribute(CoordinateSpaceAttribute);
            if (spaceAttr == null || string.IsNullOrWhiteSpace(spaceAttr.Value))
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " Rect is missing required attribute 'coordinateSpace'.");

            string value = spaceAttr.Value.Trim();
            if (string.Equals(value, "normalized", StringComparison.OrdinalIgnoreCase))
                return RectangleCoordinateSpace.Normalized;

            throw new InvalidOperationException(
                "DocuLink links LinkedRectangle #" + index + " has unsupported coordinateSpace '" + value + "'.");
        }

        private static double ReadRequiredDouble(XElement element, string attributeName, int index)
        {
            XAttribute attribute = element.Attribute(attributeName);
            if (attribute == null)
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " Rect is missing required attribute '" + attributeName + "'.");

            if (!double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new InvalidOperationException(
                    "DocuLink links LinkedRectangle #" + index + " Rect attribute '" + attributeName + "' is not a valid number.");

            return value;
        }

        private static XElement SerializeLinkedRectangle(LinkedRectangle linkedRect, int index)
        {
            if (linkedRect == null) throw new ArgumentNullException(nameof(linkedRect));
            if (string.IsNullOrWhiteSpace(linkedRect.Id))
                throw new InvalidOperationException("LinkedRectangle at index " + index + " has an empty Id.");
            if (string.IsNullOrWhiteSpace(linkedRect.PdfId))
                throw new InvalidOperationException("LinkedRectangle '" + linkedRect.Id + "' has an empty PdfId.");
            if (linkedRect.LinkedCell == null)
                throw new InvalidOperationException("LinkedRectangle '" + linkedRect.Id + "' has no LinkedCell.");
            if (linkedRect.Rectangle == null)
                throw new InvalidOperationException("LinkedRectangle '" + linkedRect.Id + "' has no Rectangle.");

            var element = new XElement(
                DocuLinkXml.LinksNs + DocuLinkXml.LinkedRectangleElementName,
                new XAttribute(IdAttribute, linkedRect.Id),
                new XAttribute(DocuLinkXml.PdfIdAttribute, linkedRect.PdfId),
                new XAttribute(LinkTypeAttribute, SerializeLinkType(linkedRect.LinkType)),
                new XElement(
                    DocuLinkXml.LinksNs + DocuLinkXml.CellElementName,
                    new XAttribute(SheetAttribute, linkedRect.LinkedCell.SheetName ?? string.Empty),
                    new XAttribute(AddressAttribute, linkedRect.LinkedCell.Address ?? string.Empty),
                    new XAttribute(TrackIndexAttribute, linkedRect.LinkedCell.TrackIndex.ToString(CultureInfo.InvariantCulture))),
                SerializeRect(linkedRect.Rectangle, linkedRect.Id));

            if (!string.IsNullOrEmpty(linkedRect.SourceText))
                element.Add(new XAttribute(SourceTextAttribute, linkedRect.SourceText));

            return element;
        }

        private static XElement SerializeRect(PdfRectangle rect, string linkedRectId)
        {
            if (rect.PageIndex < 0)
                throw new InvalidOperationException("Rectangle for LinkedRectangle '" + linkedRectId + "' has a negative page index.");

            if (rect.CoordinateSpace != RectangleCoordinateSpace.Normalized)
                throw new InvalidOperationException(
                    "Rectangle for LinkedRectangle '" + linkedRectId + "' uses a coordinate space that is not supported for serialization.");

            return new XElement(
                DocuLinkXml.LinksNs + DocuLinkXml.RectElementName,
                new XAttribute(PageAttribute, (uint)rect.PageIndex),
                new XAttribute(XAttribute, rect.X.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(YAttribute, rect.Y.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(WidthAttribute, rect.Width.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(HeightAttribute, rect.Height.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(CoordinateSpaceAttribute, "normalized"));
        }
    }
}
