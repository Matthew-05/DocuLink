using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DocuLink.Addin.Modules.Services.Conversion
{
    /// <summary>Which converter handles a given source format.</summary>
    internal enum ConversionEngine
    {
        /// <summary>Bundled Python worker (Pillow / PyMuPDF).</summary>
        Python,

        /// <summary>Late-bound Word automation.</summary>
        Word,

        /// <summary>Late-bound Excel automation (always the host instance — see OfficeInteropConverter).</summary>
        Excel,

        /// <summary>Late-bound PowerPoint automation.</summary>
        PowerPoint,

        /// <summary>Late-bound Outlook automation (saves to HTML, then rendered).</summary>
        Outlook,

        /// <summary>Rendered directly by the offscreen WebView2 host.</summary>
        Html,
    }

    /// <summary>One convertible source format.</summary>
    internal sealed class ConversionFormat
    {
        public ConversionFormat(string extension, string description, ConversionEngine engine)
        {
            Extension = extension;
            Description = description;
            Engine = engine;
        }

        /// <summary>Lower-case extension including the leading dot, e.g. ".docx".</summary>
        public string Extension { get; }

        /// <summary>Human-readable format name shown in the import dialog.</summary>
        public string Description { get; }

        public ConversionEngine Engine { get; }
    }

    /// <summary>
    /// The single source of truth for which non-PDF formats DocuLink can convert
    /// and which engine converts them.
    ///
    /// The Python-engine entries must stay in sync with the extension sets in
    /// src/python/engines/conversion_engine.py — the worker rejects anything it
    /// does not recognise, so an entry added here without a matching Python entry
    /// surfaces as a per-file conversion error rather than a silent failure.
    /// </summary>
    internal static class ConversionFormatCatalog
    {
        public const string PdfExtension = ".pdf";

        private static readonly ConversionFormat[] Formats =
        {
            // ── Images (Python / Pillow) ─────────────────────────────────────
            new ConversionFormat(".png",      "PNG image",             ConversionEngine.Python),
            new ConversionFormat(".jpg",      "JPEG image",            ConversionEngine.Python),
            new ConversionFormat(".jpeg",     "JPEG image",            ConversionEngine.Python),
            new ConversionFormat(".jpe",      "JPEG image",            ConversionEngine.Python),
            new ConversionFormat(".gif",      "GIF image",             ConversionEngine.Python),
            new ConversionFormat(".bmp",      "Bitmap image",          ConversionEngine.Python),
            new ConversionFormat(".dib",      "Bitmap image",          ConversionEngine.Python),
            new ConversionFormat(".tif",      "TIFF image",            ConversionEngine.Python),
            new ConversionFormat(".tiff",     "TIFF image",            ConversionEngine.Python),
            new ConversionFormat(".webp",     "WebP image",            ConversionEngine.Python),
            new ConversionFormat(".ico",      "Icon image",            ConversionEngine.Python),
            new ConversionFormat(".jp2",      "JPEG 2000 image",       ConversionEngine.Python),

            // ── Text and e-book formats (Python / PyMuPDF) ───────────────────
            new ConversionFormat(".txt",      "Plain text",            ConversionEngine.Python),
            new ConversionFormat(".log",      "Log file",              ConversionEngine.Python),
            new ConversionFormat(".md",       "Markdown",              ConversionEngine.Python),
            new ConversionFormat(".markdown", "Markdown",              ConversionEngine.Python),
            new ConversionFormat(".svg",      "SVG drawing",           ConversionEngine.Python),
            new ConversionFormat(".epub",     "EPUB book",             ConversionEngine.Python),
            new ConversionFormat(".mobi",     "MOBI book",             ConversionEngine.Python),
            new ConversionFormat(".fb2",      "FictionBook",           ConversionEngine.Python),
            new ConversionFormat(".cbz",      "Comic book archive",    ConversionEngine.Python),
            new ConversionFormat(".xps",      "XPS document",          ConversionEngine.Python),
            new ConversionFormat(".oxps",     "OpenXPS document",      ConversionEngine.Python),

            // ── Word ─────────────────────────────────────────────────────────
            new ConversionFormat(".doc",      "Word document",         ConversionEngine.Word),
            new ConversionFormat(".docx",     "Word document",         ConversionEngine.Word),
            new ConversionFormat(".docm",     "Word document",         ConversionEngine.Word),
            new ConversionFormat(".dot",      "Word template",         ConversionEngine.Word),
            new ConversionFormat(".dotx",     "Word template",         ConversionEngine.Word),
            new ConversionFormat(".rtf",      "Rich text document",    ConversionEngine.Word),
            new ConversionFormat(".odt",      "OpenDocument text",     ConversionEngine.Word),

            // ── Excel ────────────────────────────────────────────────────────
            new ConversionFormat(".xls",      "Excel workbook",        ConversionEngine.Excel),
            new ConversionFormat(".xlsx",     "Excel workbook",        ConversionEngine.Excel),
            new ConversionFormat(".xlsm",     "Excel workbook",        ConversionEngine.Excel),
            new ConversionFormat(".xlsb",     "Excel workbook",        ConversionEngine.Excel),
            new ConversionFormat(".csv",      "CSV data",              ConversionEngine.Excel),
            new ConversionFormat(".ods",      "OpenDocument sheet",    ConversionEngine.Excel),

            // ── PowerPoint ───────────────────────────────────────────────────
            new ConversionFormat(".ppt",      "PowerPoint deck",       ConversionEngine.PowerPoint),
            new ConversionFormat(".pptx",     "PowerPoint deck",       ConversionEngine.PowerPoint),
            new ConversionFormat(".pptm",     "PowerPoint deck",       ConversionEngine.PowerPoint),
            new ConversionFormat(".pps",      "PowerPoint show",       ConversionEngine.PowerPoint),
            new ConversionFormat(".ppsx",     "PowerPoint show",       ConversionEngine.PowerPoint),
            new ConversionFormat(".odp",      "OpenDocument slides",   ConversionEngine.PowerPoint),

            // ── Mail and markup ──────────────────────────────────────────────
            new ConversionFormat(".msg",      "Outlook message",       ConversionEngine.Outlook),
            new ConversionFormat(".eml",      "Email message",         ConversionEngine.Python),
            new ConversionFormat(".mht",      "Saved web page",        ConversionEngine.Python),
            new ConversionFormat(".mhtml",    "Saved web page",        ConversionEngine.Python),
            new ConversionFormat(".html",     "Web page",              ConversionEngine.Html),
            new ConversionFormat(".htm",      "Web page",              ConversionEngine.Html),
        };

        private static readonly Dictionary<string, ConversionFormat> ByExtension =
            Formats
                .GroupBy(f => f.Extension, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        /// <summary>All convertible formats, in catalogue order.</summary>
        public static IReadOnlyList<ConversionFormat> All => Formats;

        /// <summary>True when the path or file name already names a PDF.</summary>
        public static bool IsPdf(string pathOrName)
        {
            return string.Equals(GetExtension(pathOrName), PdfExtension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Looks up the converter for a path or file name; false when unsupported.</summary>
        public static bool TryGetFormat(string pathOrName, out ConversionFormat format)
        {
            return ByExtension.TryGetValue(GetExtension(pathOrName), out format);
        }

        /// <summary>Lower-case extension of a path or bare file name, including the dot. Empty when absent.</summary>
        public static string GetExtension(string pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName))
                return string.Empty;

            try
            {
                return (Path.GetExtension(pathOrName) ?? string.Empty).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                // Path contains invalid characters (can happen for names supplied by the
                // web layer) — fall back to a manual scan of the trailing segment.
                int dot = pathOrName.LastIndexOf('.');
                return dot >= 0 ? pathOrName.Substring(dot).ToLowerInvariant() : string.Empty;
            }
        }

        /// <summary>
        /// Builds an OpenFileDialog filter offering PDFs, every convertible format,
        /// and an all-files escape hatch.
        /// </summary>
        public static string BuildOpenFileDialogFilter()
        {
            string convertible = string.Join(
                ";",
                Formats.Select(f => "*" + f.Extension).Distinct(StringComparer.OrdinalIgnoreCase));

            var sb = new StringBuilder();
            sb.Append("Documents (*.pdf;").Append(convertible).Append(")|*.pdf;").Append(convertible);
            sb.Append("|PDF files (*.pdf)|*.pdf");
            sb.Append("|All files (*.*)|*.*");
            return sb.ToString();
        }
    }
}
