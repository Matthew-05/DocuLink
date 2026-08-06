using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DocuLink.Addin.Modules.UI;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    internal sealed class PdfImportService
    {
        private readonly AddPdfDocumentService _addService = new AddPdfDocumentService();

        public PdfImportResult ImportFilePaths(
            Excel.Workbook workbook,
            IList<PdfPathImportRequest> files,
            IProgressReporter progress)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (files == null) throw new ArgumentNullException(nameof(files));

            var result = new PdfImportResult();
            int total = files.Count;

            DocuLinkLog.Trace($"Importing {total} PDF(s) from disk.");

            for (int i = 0; i < total; i++)
            {
                PdfPathImportRequest request = files[i];

                // Progress and error text use the same name the workbook will show,
                // so a failed .docx is not reported under a scratch .pdf name.
                string fileName = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? SafeFileName(request.Path)
                    : request.DisplayName.Trim();
                int current = i + 1;

                try
                {
                    progress?.Report("Reading PDF", $"{fileName} ({current} of {total})", i, total);
                    PreparedPdf prepared = Task.Run(() => PrepareFromPath(request)).GetAwaiter().GetResult();

                    progress?.Report("Embedding in workbook", $"{prepared.Name} ({current} of {total})", i, total);
                    string id = _addService.AddPreparedPdf(
                        workbook,
                        prepared.Name,
                        prepared.Base64,
                        prepared.OcrStatus,
                        prepared.FileSizeBytes,
                        prepared.FolderId);

                    result.AddedIds.Add(id);
                    DocuLinkLog.Trace(
                        $"Embedded '{prepared.Name}' as {id} " +
                        $"({prepared.FileSizeBytes} bytes, OCR {prepared.OcrStatus}).");

                    progress?.Report("Imported PDF", $"{prepared.Name} ({current} of {total})", current, total);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{fileName}: {ex.Message}");
                    DocuLinkLog.Trace($"Could not import '{fileName}' from '{request.Path}': {ex}");
                    progress?.Report("Could not import PDF", $"{fileName} ({current} of {total})", current, total);
                }
            }

            DocuLinkLog.Trace($"Import from disk finished: {result.AddedIds.Count} added, {result.Errors.Count} failed.");
            return result;
        }

        public PdfImportResult ImportBase64(
            Excel.Workbook workbook,
            IList<PdfBase64ImportRequest> files,
            IProgressReporter progress)
        {
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            if (files == null) throw new ArgumentNullException(nameof(files));

            var result = new PdfImportResult();
            int total = files.Count;

            DocuLinkLog.Trace($"Importing {total} PDF(s) supplied as base-64.");

            for (int i = 0; i < total; i++)
            {
                PdfBase64ImportRequest request = files[i];
                string fileName = string.IsNullOrWhiteSpace(request.Name) ? "PDF" : request.Name;
                int current = i + 1;

                try
                {
                    progress?.Report("Preparing PDF", $"{fileName} ({current} of {total})", i, total);
                    PreparedPdf prepared = Task.Run(() => PrepareFromBase64(request)).GetAwaiter().GetResult();

                    progress?.Report("Embedding in workbook", $"{prepared.Name} ({current} of {total})", i, total);
                    string id = _addService.AddPreparedPdf(
                        workbook,
                        prepared.Name,
                        prepared.Base64,
                        prepared.OcrStatus,
                        prepared.FileSizeBytes,
                        prepared.FolderId);

                    result.AddedIds.Add(id);
                    DocuLinkLog.Trace(
                        $"Embedded '{prepared.Name}' as {id} " +
                        $"({prepared.FileSizeBytes} bytes, OCR {prepared.OcrStatus}).");

                    progress?.Report("Imported PDF", $"{prepared.Name} ({current} of {total})", current, total);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{fileName}: {ex.Message}");
                    DocuLinkLog.Trace($"Could not import '{fileName}' from base-64: {ex}");
                    progress?.Report("Could not import PDF", $"{fileName} ({current} of {total})", current, total);
                }
            }

            DocuLinkLog.Trace($"Import from base-64 finished: {result.AddedIds.Count} added, {result.Errors.Count} failed.");
            return result;
        }

        private static PreparedPdf PrepareFromPath(PdfPathImportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Path))
                throw new ArgumentException("PDF path must be non-empty.", nameof(request));

            byte[] bytes = File.ReadAllBytes(request.Path);
            return new PreparedPdf
            {
                Name = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? Path.GetFileName(request.Path)
                    : request.DisplayName.Trim(),
                Base64 = Convert.ToBase64String(bytes),
                FileSizeBytes = bytes.LongLength,
                FolderId = request.FolderId,
                OcrStatus = PdfTextLayerDetector.ClassifyFromBytes(bytes),
            };
        }

        private static PreparedPdf PrepareFromBase64(PdfBase64ImportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Name))
                throw new ArgumentException("PDF name must be non-empty.", nameof(request));
            if (request.Base64 == null)
                throw new ArgumentNullException(nameof(request.Base64));

            byte[] bytes = Convert.FromBase64String(request.Base64);
            return new PreparedPdf
            {
                Name = request.Name.Trim(),
                Base64 = request.Base64,
                FileSizeBytes = bytes.LongLength,
                FolderId = request.FolderId,
                OcrStatus = PdfTextLayerDetector.ClassifyFromBytes(bytes),
            };
        }

        private static string SafeFileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "PDF";

            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        private sealed class PreparedPdf
        {
            public string Name { get; set; }
            public string Base64 { get; set; }
            public string OcrStatus { get; set; }
            public long FileSizeBytes { get; set; }
            public string FolderId { get; set; }
        }
    }

    internal sealed class PdfImportResult
    {
        public IList<string> AddedIds { get; } = new List<string>();
        public IList<string> Errors { get; } = new List<string>();
    }

    internal sealed class PdfPathImportRequest
    {
        public PdfPathImportRequest(string path, string folderId = null, string displayName = null)
        {
            Path = path;
            FolderId = folderId;
            DisplayName = displayName;
        }

        public string Path { get; }
        public string FolderId { get; }

        /// <summary>
        /// Name to store in the workbook instead of the file name on disk. Set for
        /// converted documents, whose <see cref="Path"/> points at a scratch .pdf but
        /// which the user knows by their original name — 'terms.docx'. Null for a PDF
        /// imported as-is, where the file name on disk is the right answer.
        /// </summary>
        public string DisplayName { get; }
    }

    internal sealed class PdfBase64ImportRequest
    {
        public PdfBase64ImportRequest(string name, string base64, string folderId = null)
        {
            Name = name;
            Base64 = base64;
            FolderId = folderId;
        }

        public string Name { get; }
        public string Base64 { get; }
        public string FolderId { get; }
    }
}
