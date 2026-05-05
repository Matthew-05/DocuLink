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

            XElement pdfsElement = root.Element(DocuLinkXml.Ns + DocuLinkXml.PdfsElementName);
            if (pdfsElement == null)
                throw new InvalidOperationException(
                    "DocuLink storage is missing required element '" + DocuLinkXml.PdfsElementName + "'.");

            var pdfs = new List<PdfDocument>(
                pdfsElement
                    .Elements(DocuLinkXml.Ns + DocuLinkXml.PdfElementName)
                    .Select((element, idx) => ParsePdf(element, idx)));

            XElement linkedRectanglesElement = root.Element(DocuLinkXml.Ns + DocuLinkXml.LinkedRectanglesElementName);
            if (linkedRectanglesElement == null)
                throw new InvalidOperationException(
                    "DocuLink storage is missing required element '" + DocuLinkXml.LinkedRectanglesElementName + "'.");

            var linkedRectangles = new List<LinkedRectangle>(
                linkedRectanglesElement
                    .Elements(DocuLinkXml.Ns + DocuLinkXml.LinkedRectangleElementName)
                    .Select((element, idx) => ParseLinkedRectangle(element, idx)));

            return new DocuLinkStorage(fileVersion, pdfs, linkedRectangles);
        }

        public static XDocument ToXDocument(DocuLinkStorage storage)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            if (storage.Version != DocuLinkXml.SchemaVersion)
                throw new InvalidOperationException(
                    "Unsupported storage version for serialization; expected " + DocuLinkXml.SchemaVersion + ".");

            var pdfsElement = new XElement(
                DocuLinkXml.Ns + DocuLinkXml.PdfsElementName,
                storage.Pdfs.Select((pdf, i) => SerializePdf(pdf, i)));

            var linkedRectanglesElement = new XElement(
                DocuLinkXml.Ns + DocuLinkXml.LinkedRectanglesElementName,
                storage.LinkedRectangles.Select((rect, i) => SerializeLinkedRectangle(rect, i)));

            var root = new XElement(
                DocuLinkXml.Ns + DocuLinkXml.RootElementName,
                new XAttribute(VersionAttribute, DocuLinkXml.SchemaVersion),
                pdfsElement,
                linkedRectanglesElement);

            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        private static PdfDocument ParsePdf(XElement pdfElement, int index)
        {
            XAttribute idAttribute = pdfElement.Attribute(IdAttribute);
            if (idAttribute == null || string.IsNullOrWhiteSpace(idAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink storage Pdf #" + index + " is missing required attribute 'id'.");

            XAttribute base64Attribute = pdfElement.Attribute(Base64Attribute);
            if (base64Attribute == null)
                throw new InvalidOperationException(
                    "DocuLink storage Pdf #" + index + " is missing required attribute 'Base64'.");

            return new PdfDocument(idAttribute.Value.Trim(), base64Attribute.Value ?? string.Empty);
        }

        private static LinkedRectangle ParseLinkedRectangle(XElement element, int index)
        {
            XAttribute idAttribute = element.Attribute(IdAttribute);
            if (idAttribute == null || string.IsNullOrWhiteSpace(idAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " is missing required attribute 'id'.");

            XAttribute pdfIdAttribute = element.Attribute(DocuLinkXml.PdfIdAttribute);
            if (pdfIdAttribute == null || string.IsNullOrWhiteSpace(pdfIdAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " is missing required attribute 'pdfId'.");

            XElement cellElement = element.Element(DocuLinkXml.Ns + DocuLinkXml.CellElementName);
            if (cellElement == null)
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " is missing required element '" + DocuLinkXml.CellElementName + "'.");

            XAttribute sheetAttribute = cellElement.Attribute(SheetAttribute);
            XAttribute addressAttribute = cellElement.Attribute(AddressAttribute);
            if (sheetAttribute == null || string.IsNullOrWhiteSpace(sheetAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " Cell is missing required attribute 'sheet'.");
            if (addressAttribute == null || string.IsNullOrWhiteSpace(addressAttribute.Value))
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " Cell is missing required attribute 'address'.");

            XElement rectElement = element.Element(DocuLinkXml.Ns + DocuLinkXml.RectElementName);
            if (rectElement == null)
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " is missing required element '" + DocuLinkXml.RectElementName + "'.");

            XAttribute pageAttr = rectElement.Attribute(PageAttribute);
            if (pageAttr == null
                || !uint.TryParse(pageAttr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pageValue))
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " Rect is missing a valid non-negative 'page' attribute.");

            if (pageValue > int.MaxValue)
                throw new InvalidOperationException("DocuLink storage LinkedRectangle #" + index + " page index is too large.");

            RectangleCoordinateSpace space = ParseCoordinateSpace(rectElement, index);
            double x = ReadRequiredDouble(rectElement, XAttribute, index);
            double y = ReadRequiredDouble(rectElement, YAttribute, index);
            double w = ReadRequiredDouble(rectElement, WidthAttribute, index);
            double h = ReadRequiredDouble(rectElement, HeightAttribute, index);

            var rect = new PdfRectangle((int)pageValue, x, y, w, h, space);
            var cell = new LinkedCell(sheetAttribute.Value, addressAttribute.Value);

            return new LinkedRectangle(idAttribute.Value.Trim(), pdfIdAttribute.Value.Trim(), cell, rect);
        }

        private static RectangleCoordinateSpace ParseCoordinateSpace(XElement rectElement, int index)
        {
            XAttribute spaceAttr = rectElement.Attribute(CoordinateSpaceAttribute);
            if (spaceAttr == null || string.IsNullOrWhiteSpace(spaceAttr.Value))
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " Rect is missing required attribute 'coordinateSpace'.");

            string value = spaceAttr.Value.Trim();
            if (string.Equals(value, "normalized", StringComparison.OrdinalIgnoreCase))
                return RectangleCoordinateSpace.Normalized;

            throw new InvalidOperationException(
                "DocuLink storage LinkedRectangle #" + index + " has unsupported coordinateSpace '" + value + "'.");
        }

        private static double ReadRequiredDouble(XElement element, string attributeName, int index)
        {
            XAttribute attribute = element.Attribute(attributeName);
            if (attribute == null)
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " Rect is missing required attribute '" + attributeName + "'.");

            if (!double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new InvalidOperationException(
                    "DocuLink storage LinkedRectangle #" + index + " Rect attribute '" + attributeName + "' is not a valid number.");

            return value;
        }

        private static XElement SerializePdf(PdfDocument pdf, int index)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));
            if (string.IsNullOrWhiteSpace(pdf.Id))
                throw new InvalidOperationException("PdfDocument at index " + index + " has an empty Id.");

            return new XElement(
                DocuLinkXml.Ns + DocuLinkXml.PdfElementName,
                new XAttribute(IdAttribute, pdf.Id),
                new XAttribute(Base64Attribute, pdf.Base64 ?? string.Empty));
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

            return new XElement(
                DocuLinkXml.Ns + DocuLinkXml.LinkedRectangleElementName,
                new XAttribute(IdAttribute, linkedRect.Id),
                new XAttribute(DocuLinkXml.PdfIdAttribute, linkedRect.PdfId),
                new XElement(
                    DocuLinkXml.Ns + DocuLinkXml.CellElementName,
                    new XAttribute(SheetAttribute, linkedRect.LinkedCell.SheetName ?? string.Empty),
                    new XAttribute(AddressAttribute, linkedRect.LinkedCell.Address ?? string.Empty)),
                SerializeRect(linkedRect.Rectangle, linkedRect.Id));
        }

        private static XElement SerializeRect(PdfRectangle rect, string linkedRectId)
        {
            if (rect.PageIndex < 0)
                throw new InvalidOperationException("Rectangle for LinkedRectangle '" + linkedRectId + "' has a negative page index.");

            if (rect.CoordinateSpace != RectangleCoordinateSpace.Normalized)
                throw new InvalidOperationException(
                    "Rectangle for LinkedRectangle '" + linkedRectId + "' uses a coordinate space that is not supported for serialization.");

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
