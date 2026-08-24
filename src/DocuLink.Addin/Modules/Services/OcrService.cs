using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
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
        /// Queues full OCR for every requested PDF. Existing native or OCR text
        /// layers are replaced so users can rerun OCR when the stored result is
        /// stale or inaccurate.
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

            string runId = Guid.NewGuid().ToString("N");
            var batchClock = Stopwatch.StartNew();
            LogPerf(new Dictionary<string, object>
            {
                ["event"] = "batch_requested",
                ["run_id"] = runId,
                ["requested_files"] = pdfIds.Count,
            });

            if (!PythonWorkerSession.IsAvailable)
            {
                LogPerf(new Dictionary<string, object>
                {
                    ["event"] = "batch_end",
                    ["run_id"] = runId,
                    ["outcome"] = "worker-unavailable",
                    ["requested_files"] = pdfIds.Count,
                    ["total_ms"] = batchClock.ElapsedMilliseconds,
                });
                foreach (string id in pdfIds)
                    onStatusUpdate(id, "error", PythonWorkerSession.NotBuiltMessage);
                return;
            }

            var loadClock = Stopwatch.StartNew();
            var jobs = LoadJobData(pdfIds, workbook);
            loadClock.Stop();
            if (jobs.Count == 0)
            {
                LogPerf(new Dictionary<string, object>
                {
                    ["event"] = "batch_end",
                    ["run_id"] = runId,
                    ["outcome"] = "no-files-loaded",
                    ["requested_files"] = pdfIds.Count,
                    ["loaded_files"] = 0,
                    ["workbook_load_ms"] = loadClock.ElapsedMilliseconds,
                    ["total_ms"] = batchClock.ElapsedMilliseconds,
                });
                return;
            }

            var metrics = new OcrBatchMetrics(runId, pdfIds.Count, jobs.Count);
            LogPerf(new Dictionary<string, object>
            {
                ["event"] = "batch_start",
                ["run_id"] = runId,
                ["requested_files"] = pdfIds.Count,
                ["loaded_files"] = jobs.Count,
                ["input_bytes"] = jobs.Sum(job => job.InputBytes),
                ["workbook_load_ms"] = loadClock.ElapsedMilliseconds,
            });

            for (int jobIndex = 0; jobIndex < jobs.Count; jobIndex++)
            {
                onStatusUpdate(
                    jobs[jobIndex].PdfId,
                    "queued",
                    $"Waiting — file {jobIndex + 1} of {jobs.Count}");
            }

            _cts = new CancellationTokenSource();
            IsRunning = true;
            try
            {
                await Task.Run(() => RunWorker(
                    jobs, workbook, onStatusUpdate, _cts.Token, metrics));
            }
            catch (Exception ex)
            {
                metrics.UnhandledError = ex.GetType().Name + ": " + ex.Message;
                throw;
            }
            finally
            {
                batchClock.Stop();
                var batchEnd = new Dictionary<string, object>
                {
                    ["event"] = "batch_end",
                    ["run_id"] = runId,
                    ["outcome"] = string.IsNullOrEmpty(metrics.UnhandledError)
                        ? (metrics.Cancelled > 0 ? "cancelled" : "complete")
                        : "error",
                    ["requested_files"] = metrics.Requested,
                    ["loaded_files"] = metrics.Loaded,
                    ["succeeded"] = metrics.Succeeded,
                    ["failed"] = metrics.Failed,
                    ["cancelled"] = metrics.Cancelled,
                    ["skipped"] = metrics.Skipped,
                    ["input_bytes"] = jobs.Sum(job => job.InputBytes),
                    ["workbook_load_ms"] = loadClock.ElapsedMilliseconds,
                    ["worker_start_ms"] = metrics.WorkerStartMs,
                    ["total_ms"] = batchClock.ElapsedMilliseconds,
                };
                if (!string.IsNullOrEmpty(metrics.UnhandledError))
                    batchEnd["error"] = metrics.UnhandledError;
                LogPerf(batchEnd);

                IsRunning = false;
                _cts.Dispose();
                _cts = null;
            }
        }

        private void RunWorker(
            IList<OcrJobEntry> jobs,
            Excel.Workbook workbook,
            Action<string, string, string> onStatusUpdate,
            CancellationToken token,
            OcrBatchMetrics metrics)
        {
            using (var session = new PythonWorkerSession())
            {
                var startClock = Stopwatch.StartNew();
                session.Start();
                startClock.Stop();
                metrics.WorkerStartMs = startClock.ElapsedMilliseconds;
                LogPerf(new Dictionary<string, object>
                {
                    ["event"] = "worker_started",
                    ["run_id"] = metrics.RunId,
                    ["startup_ms"] = metrics.WorkerStartMs,
                });
                _runningSession = session;

                try
                {
                    for (int jobIndex = 0; jobIndex < jobs.Count; jobIndex++)
                    {
                        OcrJobEntry job = jobs[jobIndex];
                        var jobClock = Stopwatch.StartNew();

                        // Cancelled or worker already gone — revert remaining to original status
                        if (token.IsCancellationRequested || session.IsDead)
                        {
                            bool cancelled = token.IsCancellationRequested;
                            var callbackClock = Stopwatch.StartNew();
                            Invoke(() => onStatusUpdate(job.PdfId, job.OriginalStatus, null));
                            callbackClock.Stop();
                            if (cancelled) metrics.Cancelled++;
                            else metrics.Skipped++;
                            LogJobOutcome(
                                metrics.RunId, jobIndex, jobs.Count, job,
                                cancelled ? "cancelled" : "skipped-worker-dead",
                                cancelled ? null : "Worker was unavailable after a prior job.",
                                null, 0, 0, 0, 0, 0,
                                callbackClock.ElapsedMilliseconds,
                                jobClock.ElapsedMilliseconds);
                            continue;
                        }

                        var processingCallbackClock = Stopwatch.StartNew();
                        Invoke(() => onStatusUpdate(
                            job.PdfId,
                            "processing",
                            $"Starting file {jobIndex + 1} of {jobs.Count}…"));
                        processingCallbackClock.Stop();

                        var buildClock = Stopwatch.StartNew();
                        string jobJson = BuildJobJson(job);
                        buildClock.Stop();

                        var sendClock = Stopwatch.StartNew();
                        session.SendJob(jobJson);
                        sendClock.Stop();

                        // Read lines until we get a terminal result for this job_id
                        var waitClock = Stopwatch.StartNew();
                        string lastProgressStage = null;
                        var progressLogClock = Stopwatch.StartNew();
                        string resultLine = session.ReadResultLine(
                            job.PdfId,
                            message =>
                            {
                                // Worker progress used to stop at the performance log.
                                // Forward every stage/page update to the file manager;
                                // the host serializer adds stable stage/count fields.
                                Invoke(() => onStatusUpdate(
                                    job.PdfId,
                                    "processing",
                                    message));

                                string stage = ProgressStage(message);
                                if (string.Equals(stage, lastProgressStage, StringComparison.Ordinal)
                                    && progressLogClock.ElapsedMilliseconds < 5000)
                                    return;

                                lastProgressStage = stage;
                                progressLogClock.Restart();
                                LogPerf(new Dictionary<string, object>
                                {
                                    ["event"] = "progress",
                                    ["run_id"] = metrics.RunId,
                                    ["job_index"] = jobIndex + 1,
                                    ["job_count"] = jobs.Count,
                                    ["pdf_id"] = job.PdfId,
                                    ["name"] = job.Name,
                                    ["stage"] = stage,
                                    ["message"] = message,
                                    ["elapsed_ms"] = jobClock.ElapsedMilliseconds,
                                });
                            });
                        waitClock.Stop();

                        if (resultLine == null)
                        {
                            // Stream closed — either we killed the process (cancellation) or it crashed
                            bool cancelled = token.IsCancellationRequested;
                            var callbackClock = Stopwatch.StartNew();
                            Invoke(() => onStatusUpdate(
                                job.PdfId,
                                cancelled ? job.OriginalStatus : "error",
                                cancelled ? null : "Worker closed unexpectedly."));
                            callbackClock.Stop();
                            if (cancelled) metrics.Cancelled++;
                            else metrics.Failed++;
                            LogJobOutcome(
                                metrics.RunId, jobIndex, jobs.Count, job,
                                cancelled ? "cancelled" : "worker-died",
                                cancelled ? null : "Worker closed unexpectedly.",
                                null,
                                buildClock.ElapsedMilliseconds,
                                sendClock.ElapsedMilliseconds,
                                waitClock.ElapsedMilliseconds,
                                0, 0,
                                processingCallbackClock.ElapsedMilliseconds
                                    + callbackClock.ElapsedMilliseconds,
                                jobClock.ElapsedMilliseconds);
                            continue;
                        }

                        var parseClock = Stopwatch.StartNew();
                        var parsed = ParseResultLine(resultLine);
                        parseClock.Stop();

                        long storageMs = 0;
                        long callbackMs = processingCallbackClock.ElapsedMilliseconds;
                        string outcome = parsed.Status;
                        string error = parsed.Error;

                        if (parsed.Status == "success")
                        {
                            Invoke(() =>
                            {
                                var storageClock = Stopwatch.StartNew();
                                try
                                {
                                    if (string.Equals(job.Mode, "geometry-only", StringComparison.Ordinal)
                                        || job.PreserveSourcePdf)
                                    {
                                        _manageService.UpdatePdfGeometry(
                                            workbook, job.PdfId, parsed.GeometryBase64 ?? string.Empty);
                                    }
                                    else
                                    {
                                        _manageService.UpdatePdfAfterOcr(
                                            workbook,
                                            job.PdfId,
                                            parsed.PdfBase64 ?? string.Empty,
                                            parsed.GeometryBase64 ?? string.Empty);
                                    }
                                    storageClock.Stop();
                                    storageMs = storageClock.ElapsedMilliseconds;

                                    var callbackClock = Stopwatch.StartNew();
                                    onStatusUpdate(job.PdfId, PdfStatus.Ocr, null);
                                    callbackClock.Stop();
                                    callbackMs += callbackClock.ElapsedMilliseconds;
                                }
                                catch (Exception ex)
                                {
                                    storageClock.Stop();
                                    storageMs = storageClock.ElapsedMilliseconds;
                                    outcome = "storage-error";
                                    error = ex.Message;
                                    var callbackClock = Stopwatch.StartNew();
                                    onStatusUpdate(job.PdfId, "error", ex.Message);
                                    callbackClock.Stop();
                                    callbackMs += callbackClock.ElapsedMilliseconds;
                                }
                            });
                        }
                        else
                        {
                            var callbackClock = Stopwatch.StartNew();
                            Invoke(() => onStatusUpdate(job.PdfId, "error", parsed.Error));
                            callbackClock.Stop();
                            callbackMs += callbackClock.ElapsedMilliseconds;
                        }

                        jobClock.Stop();
                        if (string.Equals(outcome, "success", StringComparison.Ordinal))
                            metrics.Succeeded++;
                        else
                            metrics.Failed++;

                        LogJobOutcome(
                            metrics.RunId, jobIndex, jobs.Count, job, outcome, error,
                            parsed.Diagnostics,
                            buildClock.ElapsedMilliseconds,
                            sendClock.ElapsedMilliseconds,
                            waitClock.ElapsedMilliseconds,
                            parseClock.ElapsedMilliseconds,
                            storageMs,
                            callbackMs,
                            jobClock.ElapsedMilliseconds,
                            parsed.PdfBase64,
                            parsed.GeometryBase64);
                    }
                }
                finally
                {
                    _runningSession = null;
                }
            }
        }

        /// <summary>
        /// Writes a machine-readable, single-line performance record. The OCR_PERF
        /// prefix makes a large mixed debug log easy to filter before analysis.
        /// </summary>
        private static void LogJobOutcome(
            string runId,
            int jobIndex,
            int jobCount,
            OcrJobEntry job,
            string outcome,
            string error,
            Dictionary<string, object> diagnostics,
            long buildJsonMs,
            long sendMs,
            long waitMs,
            long parseMs,
            long storageMs,
            long callbackMs,
            long totalMs,
            string outputPdfBase64 = null,
            string geometryBase64 = null)
        {
            var record = new Dictionary<string, object>
            {
                ["event"] = "job_end",
                ["run_id"] = runId,
                ["job_index"] = jobIndex + 1,
                ["job_count"] = jobCount,
                ["pdf_id"] = job.PdfId,
                ["name"] = job.Name,
                ["mode"] = job.Mode,
                ["outcome"] = outcome,
                ["input_bytes"] = job.InputBytes,
                ["output_pdf_bytes"] = Base64DecodedLength(outputPdfBase64),
                ["geometry_bytes"] = Base64DecodedLength(geometryBase64),
                ["workbook_binary_load_ms"] = job.LoadMs,
                ["host"] = new Dictionary<string, object>
                {
                    ["build_json_ms"] = buildJsonMs,
                    ["send_ms"] = sendMs,
                    ["wait_ms"] = waitMs,
                    ["parse_ms"] = parseMs,
                    ["workbook_storage_ms"] = storageMs,
                    ["ui_callback_ms"] = callbackMs,
                    ["total_ms"] = totalMs,
                },
            };
            if (diagnostics != null)
                record["worker"] = diagnostics;
            if (!string.IsNullOrEmpty(error))
                record["error"] = error;
            LogPerf(record);
        }

        private static void LogPerf(Dictionary<string, object> record)
        {
            try
            {
                string json = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue,
                }.Serialize(record);
                DocuLinkLog.Trace("OCR_PERF " + json);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace("OCR_PERF serialization-error=" + ex.Message);
            }
        }

        internal static string ProgressStage(string message)
        {
            string value = message ?? string.Empty;
            if (value.StartsWith("Extracting geometry", StringComparison.OrdinalIgnoreCase))
                return "geometry";
            if (value.StartsWith("Direct OCR", StringComparison.OrdinalIgnoreCase))
                return "ocr";
            if (value.StartsWith("Retrying", StringComparison.OrdinalIgnoreCase))
                return "retry";
            if (value.StartsWith("Identical PDF", StringComparison.OrdinalIgnoreCase))
                return "cache";
            if (value.StartsWith("Source text", StringComparison.OrdinalIgnoreCase))
                return "source";
            if (value.IndexOf("high-resolution layout", StringComparison.OrdinalIgnoreCase) >= 0)
                return "adaptive-evaluation";
            if (value.StartsWith("Retrying OCR", StringComparison.OrdinalIgnoreCase))
                return "adaptive-ocr";
            if (value.IndexOf("table", StringComparison.OrdinalIgnoreCase) >= 0
                || value.StartsWith("Recovering", StringComparison.OrdinalIgnoreCase))
                return "table-recovery";
            if (value.IndexOf("rasterizing", StringComparison.OrdinalIgnoreCase) >= 0)
                return "forced-ocr";
            if (value.IndexOf("OCR", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ocr";
            return "worker";
        }

        internal static bool TryParseProgressCount(
            string message,
            out int current,
            out int total)
        {
            current = 0;
            total = 0;
            Match match = Regex.Match(
                message ?? string.Empty,
                @"(?:\(|\b)(\d+)\s+of\s+(\d+)(?:\)|\b)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success
                && int.TryParse(match.Groups[1].Value, out current)
                && int.TryParse(match.Groups[2].Value, out total)
                && total > 0;
        }

        private static long Base64DecodedLength(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            long padding = value.EndsWith("==", StringComparison.Ordinal) ? 2
                : value.EndsWith("=", StringComparison.Ordinal) ? 1 : 0;
            return (value.Length / 4L) * 3L - padding;
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
                        Diagnostics = PythonWorkerSession.GetDictionary(obj, "diagnostics"),
                    };
                }

                string err = PythonWorkerSession.GetString(obj, "error");
                return new OcrWorkerResult
                {
                    Status = "error",
                    Error = string.IsNullOrEmpty(err) ? "Unknown error" : err,
                    Diagnostics = PythonWorkerSession.GetDictionary(obj, "diagnostics"),
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
            sb.Append(",\"preserve_source_pdf\":");
            sb.Append(job.PreserveSourcePdf ? "true" : "false");
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
                var loadClock = Stopwatch.StartNew();
                store.TryLoadPdfBinary(id, out string base64, out _);
                loadClock.Stop();
                result.Add(new OcrJobEntry
                {
                    PdfId = id,
                    Name = metadata.Name ?? string.Empty,
                    Base64 = base64 ?? string.Empty,
                    InputBytes = Base64DecodedLength(base64),
                    LoadMs = loadClock.ElapsedMilliseconds,
                    Mode = "full",
                    PreserveSourcePdf = true,
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
            public string Name { get; set; }
            public string Base64 { get; set; }
            public long InputBytes { get; set; }
            public long LoadMs { get; set; }
            public string Mode { get; set; }
            public bool PreserveSourcePdf { get; set; }
            public string OriginalStatus { get; set; }
        }

        private sealed class OcrBatchMetrics
        {
            public OcrBatchMetrics(string runId, int requested, int loaded)
            {
                RunId = runId;
                Requested = requested;
                Loaded = loaded;
            }

            public string RunId { get; }
            public int Requested { get; }
            public int Loaded { get; }
            public int Succeeded { get; set; }
            public int Failed { get; set; }
            public int Cancelled { get; set; }
            public int Skipped { get; set; }
            public long WorkerStartMs { get; set; }
            public string UnhandledError { get; set; }
        }

        private sealed class OcrWorkerResult
        {
            public string Status { get; set; }
            public string PdfBase64 { get; set; }
            public string GeometryBase64 { get; set; }
            public string Error { get; set; }

            /// <summary>
            /// Optional OcrDiagnostics object from the worker. Null when the worker omitted
            /// it or the line failed to parse — debug logging only, never behavioural.
            /// </summary>
            public Dictionary<string, object> Diagnostics { get; set; }
        }
    }
}
