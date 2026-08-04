using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DocuLink.Addin.Modules.CustomXml.Models;
using DocuLink.Addin.Modules.Infrastructure;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Runs OCR on one or more PDFs stored in the workbook by delegating to the
    /// bundled Python worker process via a stdin/stdout JSON-line protocol.
    ///
    /// Threading contract:
    ///   • <see cref="RunOcrAsync"/> must be called on the UI thread.
    ///   • Phase 1 (Excel COM reads) executes synchronously on the UI thread.
    ///   • Phase 2 (process I/O) executes on a <see cref="Task.Run"/> background thread.
    ///   • Phase 3 (storage writes + callbacks) marshals back to the UI thread
    ///     via the <see cref="Control"/> handle supplied to the constructor.
    /// </summary>
    public sealed class OcrService
    {
        private readonly Control _uiControl;
        private readonly ManageFilesService _manageService = new ManageFilesService();

        // Cancellation state — _runningSession is volatile so Cancel() (any thread) always
        // reads the latest value written by the background worker thread.
        private volatile PythonWorkerSession _runningSession;
        private CancellationTokenSource _cts;

        /// <summary>True while a RunOcrAsync call is in flight. UI-thread only.</summary>
        public bool IsRunning { get; private set; }

        /// <param name="uiControl">
        /// Any WinForms control that lives on the UI thread (e.g. the hosting
        /// <see cref="Form"/>). Used to marshal callbacks back to the UI thread.
        /// </param>
        public OcrService(Control uiControl)
        {
            _uiControl = uiControl ?? throw new ArgumentNullException(nameof(uiControl));
        }

        /// <summary>
        /// Cancels any in-progress OCR run. The worker process is killed immediately;
        /// the in-flight job and all remaining queued jobs are reverted to their
        /// original status (none / text) rather than marked as errors.
        /// Safe to call from any thread.
        /// </summary>
        public void Cancel()
        {
            _cts?.Cancel();
            _runningSession?.Kill();
        }

        /// <summary>
        /// Queues OCR for the given PDF ids. Scanned PDFs (status none) and PDFs
        /// with an embedded text layer (status text) both receive full OCR so a
        /// stale or mismatched text layer is replaced rather than trusted.
        /// Already-processed PDFs (status ocr) are skipped.
        /// </summary>
        public Task RunOcrAsync(
            IList<string> pdfIds,
            Excel.Workbook workbook,
            Action<string, string, string> onStatusUpdate)
        {
            return RunJobsAsync(pdfIds, workbook, onStatusUpdate);
        }

        private async Task RunJobsAsync(
            IList<string> pdfIds,
            Excel.Workbook workbook,
            Action<string, string, string> onStatusUpdate)
        {
            if (pdfIds == null || pdfIds.Count == 0) return;
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));
            WorkbookProtectionGuard.ThrowIfStructureProtected(workbook);

            if (!PythonWorkerSession.IsAvailable)
            {
                foreach (string id in pdfIds)
                    onStatusUpdate(id, "error", PythonWorkerSession.NotBuiltMessage);
                return;
            }

            var jobs = LoadJobData(pdfIds, workbook);
            if (jobs.Count == 0) return;

            foreach (var job in jobs)
                onStatusUpdate(job.PdfId, "queued", null);

            _cts = new CancellationTokenSource();
            IsRunning = true;
            try
            {
                await Task.Run(() => RunWorker(jobs, workbook, onStatusUpdate, _cts.Token));
            }
            finally
            {
                IsRunning = false;
                _cts.Dispose();
                _cts = null;
            }
        }

        private void RunWorker(
            IList<OcrJobEntry> jobs,
            Excel.Workbook workbook,
            Action<string, string, string> onStatusUpdate,
            CancellationToken token)
        {
            using (var session = new PythonWorkerSession())
            {
                session.Start();
                _runningSession = session;

                try
                {
                    foreach (var job in jobs)
                    {
                        // Cancelled or worker already gone — revert remaining to original status
                        if (token.IsCancellationRequested || session.IsDead)
                        {
                            Invoke(() => onStatusUpdate(job.PdfId, job.OriginalStatus, null));
                            continue;
                        }

                        Invoke(() => onStatusUpdate(job.PdfId, "processing", null));

                        session.SendJob(BuildJobJson(job));

                        // Read lines until we get a terminal result for this job_id
                        string resultLine = session.ReadResultLine(job.PdfId);

                        if (resultLine == null)
                        {
                            // Stream closed — either we killed the process (cancellation) or it crashed
                            bool cancelled = token.IsCancellationRequested;
                            Invoke(() => onStatusUpdate(
                                job.PdfId,
                                cancelled ? job.OriginalStatus : "error",
                                cancelled ? null : "Worker closed unexpectedly."));
                            continue;
                        }

                        var parsed = ParseResultLine(resultLine);

                        if (parsed.Status == "success")
                        {
                            Invoke(() =>
                            {
                                try
                                {
                                    if (string.Equals(job.Mode, "geometry-only", StringComparison.Ordinal))
                                    {
                                        _manageService.UpdatePdfGeometry(
                                            workbook, job.PdfId, parsed.GeometryBase64 ?? string.Empty);
                                        onStatusUpdate(job.PdfId, PdfStatus.Ocr, null);
                                    }
                                    else
                                    {
                                        _manageService.UpdatePdfAfterOcr(
                                            workbook,
                                            job.PdfId,
                                            parsed.PdfBase64 ?? string.Empty,
                                            parsed.GeometryBase64 ?? string.Empty);
                                        onStatusUpdate(job.PdfId, PdfStatus.Ocr, null);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    onStatusUpdate(job.PdfId, "error", ex.Message);
                                }
                            });
                        }
                        else
                        {
                            Invoke(() => onStatusUpdate(job.PdfId, "error", parsed.Error));
                        }
                    }
                }
                finally
                {
                    _runningSession = null;
                }
            }
        }

        private static OcrWorkerResult ParseResultLine(string line)
        {
            try
            {
                var obj = PythonWorkerSession.Deserialize(line);
                string status = PythonWorkerSession.GetString(obj, "status");

                if (status == "success")
                {
                    return new OcrWorkerResult
                    {
                        Status = "success",
                        PdfBase64 = PythonWorkerSession.GetString(obj, "pdf_base64"),
                        GeometryBase64 = PythonWorkerSession.GetString(obj, "geometry_base64"),
                    };
                }

                string err = PythonWorkerSession.GetString(obj, "error");
                return new OcrWorkerResult
                {
                    Status = "error",
                    Error = string.IsNullOrEmpty(err) ? "Unknown error" : err,
                };
            }
            catch (Exception ex)
            {
                return new OcrWorkerResult
                {
                    Status = "error",
                    Error = "Failed to parse worker response: " + ex.Message,
                };
            }
        }

        private static string BuildJobJson(OcrJobEntry job)
        {
            var sb = new StringBuilder();
            sb.Append("{\"job_id\":");
            PythonWorkerSession.AppendJsonString(sb, job.PdfId);
            sb.Append(",\"command\":\"ocr\",\"pdf_base64\":");
            PythonWorkerSession.AppendJsonString(sb, job.Base64);
            sb.Append(",\"mode\":");
            PythonWorkerSession.AppendJsonString(sb, job.Mode ?? "full");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Reads the base64 bytes for the requested PDFs from the workbook and assigns
        /// full OCR mode. Must be called on the UI thread.
        /// </summary>
        private static IList<OcrJobEntry> LoadJobData(IList<string> pdfIds, Excel.Workbook workbook)
        {
            var store = new CustomXml.DocuLinkCustomXmlPartStore(workbook);
            DocuLinkContent content = store.LoadContent(); // metadata only — fast

            var result = new List<OcrJobEntry>();
            foreach (string id in pdfIds)
            {
                var metadata = content.Pdfs.FirstOrDefault(
                    p => string.Equals(p.Id, id, StringComparison.Ordinal));
                if (metadata == null) continue;

                string status = metadata.OcrStatus ?? PdfStatus.None;
                if (string.Equals(status, PdfStatus.Ocr, StringComparison.Ordinal)) continue;

                // Load binary only for PDFs that actually need OCR
                store.TryLoadPdfBinary(id, out string base64, out _);
                result.Add(new OcrJobEntry
                {
                    PdfId = id,
                    Base64 = base64 ?? string.Empty,
                    Mode = "full",
                    OriginalStatus = status,
                });
            }
            return result;
        }

        private void Invoke(Action action)
        {
            if (_uiControl.InvokeRequired)
                _uiControl.Invoke(action);
            else
                action();
        }

        private sealed class OcrJobEntry
        {
            public string PdfId { get; set; }
            public string Base64 { get; set; }
            public string Mode { get; set; }
            public string OriginalStatus { get; set; }
        }

        private sealed class OcrWorkerResult
        {
            public string Status { get; set; }
            public string PdfBase64 { get; set; }
            public string GeometryBase64 { get; set; }
            public string Error { get; set; }
        }
    }
}
