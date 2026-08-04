using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DocuLink.Addin.Modules.UI;

namespace DocuLink.Addin.Modules.Services.Conversion
{
    /// <summary>One non-PDF document queued for conversion.</summary>
    internal sealed class DocumentConversionRequest
    {
        /// <summary>Source on disk. Null when the document arrived as bytes from the web layer.</summary>
        public string SourcePath { get; set; }

        /// <summary>Source bytes. Null when <see cref="SourcePath"/> is set.</summary>
        public byte[] SourceBytes { get; set; }

        /// <summary>File name including extension, used for naming and format lookup.</summary>
        public string FileName { get; set; }

        /// <summary>Target folder id in the workbook, carried through unchanged.</summary>
        public string FolderId { get; set; }
    }

    /// <summary>A successfully converted document, as a PDF on disk in temp storage.</summary>
    internal sealed class DocumentConversionSuccess
    {
        public string PdfPath { get; set; }

        /// <summary>
        /// Name to store in the workbook: the source file name exactly as the user
        /// knows it, extension and all. 'terms.docx' stays 'terms.docx' even though
        /// its stored content is now a PDF — only <see cref="PdfPath"/>, the scratch
        /// file actually read from disk, carries the .pdf extension.
        /// </summary>
        public string DisplayName { get; set; }

        public string FolderId { get; set; }
    }

    /// <summary>Outcome of a conversion batch.</summary>
    internal sealed class DocumentConversionResult
    {
        public IList<DocumentConversionSuccess> Converted { get; } = new List<DocumentConversionSuccess>();

        /// <summary>"file.docx: Microsoft Word is not installed…" style messages, one per failure.</summary>
        public IList<string> Errors { get; } = new List<string>();
    }

    /// <summary>
    /// Turns non-PDF documents into PDFs in temp storage so the rest of the import
    /// pipeline only ever deals with PDFs.
    ///
    /// Routing lives in <see cref="ConversionFormatCatalog"/>; this service owns the
    /// batch lifecycle — it starts each engine at most once, converts every file,
    /// and guarantees the engines are shut down even when a file fails.
    ///
    /// The returned PDFs live in the caller's <see cref="ConversionTempStore"/> and
    /// vanish when that store is disposed, which the caller does immediately after
    /// embedding them in the workbook.
    ///
    /// Must be awaited on the UI thread: HTML rendering drives an offscreen WebView2,
    /// which needs a message pump. Worker and Office calls are pushed onto a
    /// background thread so the pump keeps running.
    /// </summary>
    internal sealed class DocumentConversionService
    {
        private readonly ConversionTempStore _tempStore;

        public DocumentConversionService(ConversionTempStore tempStore)
        {
            _tempStore = tempStore ?? throw new ArgumentNullException(nameof(tempStore));
        }

        public async Task<DocumentConversionResult> ConvertAsync(
            IList<DocumentConversionRequest> requests,
            IProgressReporter progress)
        {
            var result = new DocumentConversionResult();
            if (requests == null || requests.Count == 0)
                return result;

            int total = requests.Count;

            var office = new OfficeInteropConverter();

            try
            {
                using (var python = new PythonConversionConverter())
                {
                    for (int i = 0; i < total; i++)
                    {
                        DocumentConversionRequest request = requests[i];
                        string displayName = string.IsNullOrWhiteSpace(request.FileName)
                            ? "document"
                            : request.FileName;
                        int current = i + 1;

                        progress?.Report("Converting to PDF", $"{displayName} ({current} of {total})", i, total);

                        try
                        {
                            DocumentConversionSuccess converted =
                                await ConvertOneAsync(request, office, python);
                            result.Converted.Add(converted);
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"{displayName}: {ex.Message}");
                            DocuLinkLog.Trace($"Conversion failed for '{displayName}': {ex}");
                        }
                    }
                }
            }
            finally
            {
                // Awaited rather than disposed: quitting Office takes seconds, and we
                // are on the UI thread, whose message pump must keep running.
                progress?.Report("Closing converters", null, total, total);
                await office.ShutdownAsync();
            }

            return result;
        }

        private async Task<DocumentConversionSuccess> ConvertOneAsync(
            DocumentConversionRequest request,
            OfficeInteropConverter office,
            PythonConversionConverter python)
        {
            string fileName = request.FileName;
            if (string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(request.SourcePath))
                fileName = Path.GetFileName(request.SourcePath);

            if (!ConversionFormatCatalog.TryGetFormat(fileName, out ConversionFormat format))
                throw new NotSupportedException("This file type cannot be converted to PDF.");

            string baseName = Path.GetFileNameWithoutExtension(fileName ?? "document");
            string outputPdfPath = _tempStore.ReservePath(baseName, ConversionFormatCatalog.PdfExtension);

            switch (format.Engine)
            {
                case ConversionEngine.Python:
                    await ConvertViaPythonAsync(request, format, python, baseName, outputPdfPath);
                    break;

                case ConversionEngine.Html:
                    await ConvertViaHtmlAsync(request, format, baseName, outputPdfPath);
                    break;

                default:
                    await ConvertViaOfficeAsync(request, format, office, baseName, outputPdfPath);
                    break;
            }

            if (!File.Exists(outputPdfPath) || new FileInfo(outputPdfPath).Length == 0)
                throw new IOException("Conversion produced no output.");

            return new DocumentConversionSuccess
            {
                PdfPath = outputPdfPath,

                // Only fall back to a .pdf name when the source had no usable name at
                // all — otherwise the user sees the file they picked, not a rename.
                DisplayName = string.IsNullOrWhiteSpace(fileName)
                    ? baseName + ConversionFormatCatalog.PdfExtension
                    : fileName,

                FolderId = request.FolderId,
            };
        }

        private async Task ConvertViaPythonAsync(
            DocumentConversionRequest request,
            ConversionFormat format,
            PythonConversionConverter python,
            string baseName,
            string outputPdfPath)
        {
            if (!PythonConversionConverter.IsAvailable)
                throw new InvalidOperationException(PythonConversionConverter.UnavailableMessage);

            byte[] sourceBytes = ReadSourceBytes(request);

            PythonConversionResult converted = await Task.Run(
                () => python.Convert(sourceBytes, format.Extension, request.FileName));

            if (string.Equals(converted.Kind, "pdf", StringComparison.Ordinal))
            {
                File.WriteAllBytes(outputPdfPath, converted.PdfBytes);
                return;
            }

            // The worker could only normalise the source to HTML (.eml / .mht) —
            // render it here, where the WebView2 lives.
            string htmlPath = _tempStore.ReservePath(baseName, ".html");
            File.WriteAllText(htmlPath, converted.Html ?? string.Empty, System.Text.Encoding.UTF8);

            try
            {
                await HtmlToPdfConverter.ConvertFileAsync(htmlPath, outputPdfPath);
            }
            finally
            {
                _tempStore.DeleteNow(htmlPath);
            }
        }

        private async Task ConvertViaHtmlAsync(
            DocumentConversionRequest request,
            ConversionFormat format,
            string baseName,
            string outputPdfPath)
        {
            string htmlPath = request.SourcePath;
            bool htmlIsTemp = false;

            if (string.IsNullOrWhiteSpace(htmlPath))
            {
                htmlPath = _tempStore.WriteSource(baseName, format.Extension, request.SourceBytes);
                htmlIsTemp = true;
            }

            try
            {
                await HtmlToPdfConverter.ConvertFileAsync(htmlPath, outputPdfPath);
            }
            finally
            {
                if (htmlIsTemp)
                    _tempStore.DeleteNow(htmlPath);
            }
        }

        private async Task ConvertViaOfficeAsync(
            DocumentConversionRequest request,
            ConversionFormat format,
            OfficeInteropConverter office,
            string baseName,
            string outputPdfPath)
        {
            if (!OfficeInteropConverter.IsAvailable(format.Engine))
                throw new InvalidOperationException(
                    $"{OfficeInteropConverter.GetApplicationName(format.Engine)} is not installed, " +
                    "so this file type cannot be converted.");

            // Office automation only opens real files, so bytes from the web layer
            // are spilled to temp first.
            string sourcePath = request.SourcePath;
            bool sourceIsTemp = false;

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                sourcePath = _tempStore.WriteSource(baseName, format.Extension, request.SourceBytes);
                sourceIsTemp = true;
            }

            string messageHtmlPath = format.Engine == ConversionEngine.Outlook
                ? _tempStore.ReservePath(baseName, ".html")
                : null;

            string producedHtmlPath = null;

            try
            {
                // Deliberately not Task.Run — the converter owns a dedicated STA
                // thread with its own message loop and marshals the COM work there
                // itself. Running it on a pool thread produces the 'DisconnectedContext'
                // MDA, since the cached application RCWs outlive the pool thread's
                // COM context.
                producedHtmlPath = await office.ConvertAsync(
                    format.Engine,
                    sourcePath,
                    outputPdfPath,
                    messageHtmlPath);

                // Outlook exports HTML rather than PDF; finish the job here.
                if (!string.IsNullOrEmpty(producedHtmlPath))
                    await HtmlToPdfConverter.ConvertFileAsync(producedHtmlPath, outputPdfPath);
            }
            finally
            {
                if (sourceIsTemp)
                    _tempStore.DeleteNow(sourcePath);

                if (!string.IsNullOrEmpty(messageHtmlPath))
                {
                    // Outlook writes a sibling "<name>_files" folder alongside the HTML.
                    DeleteSidecarFolder(messageHtmlPath);
                    _tempStore.DeleteNow(messageHtmlPath);
                }
            }
        }

        private static byte[] ReadSourceBytes(DocumentConversionRequest request)
        {
            if (request.SourceBytes != null)
                return request.SourceBytes;

            if (string.IsNullOrWhiteSpace(request.SourcePath))
                throw new InvalidOperationException("No source content was supplied.");

            return File.ReadAllBytes(request.SourcePath);
        }

        private static void DeleteSidecarFolder(string htmlPath)
        {
            try
            {
                string sidecar = Path.Combine(
                    Path.GetDirectoryName(htmlPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(htmlPath) + "_files");

                if (Directory.Exists(sidecar))
                    Directory.Delete(sidecar, recursive: true);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Could not delete Outlook sidecar folder for '{htmlPath}': {ex.Message}");
            }
        }
    }
}
