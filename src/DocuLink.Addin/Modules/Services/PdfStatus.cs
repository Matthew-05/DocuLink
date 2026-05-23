using System;

namespace DocuLink.Addin.Modules.Services
{
    internal static class PdfStatus
    {
        public const string Ocr  = "ocr";
        public const string Text = "text";
        public const string None = "none";

        /// <summary>
        /// Normalizes legacy or in-flight status values to the persisted set: ocr, text, none.
        /// </summary>
        public static string NormalizeStored(string rawStatus, string base64, string geometryBase64)
        {
            if (!string.IsNullOrWhiteSpace(geometryBase64))
                return Ocr;

            if (string.IsNullOrWhiteSpace(rawStatus))
                return ClassifyOrNone(base64);

            switch (rawStatus.Trim().ToLowerInvariant())
            {
                case Ocr:
                case "complete":
                case "enhanced":
                    return Ocr;
                case Text:
                    return Text;
                case None:
                    return None;
                default:
                    return ClassifyOrNone(base64);
            }
        }

        private static string ClassifyOrNone(string base64)
        {
            return PdfTextLayerDetector.ClassifyFromBase64(base64);
        }
    }
}
