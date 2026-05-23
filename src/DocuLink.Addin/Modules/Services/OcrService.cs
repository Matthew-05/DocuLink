using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
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

        /// <param name="uiControl">
        /// Any WinForms control that lives on the UI thread (e.g. the hosting
        /// <see cref="Form"/>). Used to marshal callbacks back to the UI thread.
        /// </param>
        public OcrService(Control uiControl)
        {
            _uiControl = uiControl ?? throw new ArgumentNullException(nameof(uiControl));
        }

        /// <summary>
        /// Queues full OCR for the given PDF ids.
        /// </summary>
        public Task RunOcrAsync(
            IList<string> pdfIds,
            Excel.Workbook workbook,
            Action<string, string, string> onStatusUpdate)
        {
            return RunJobsAsync(pdfIds, workbook, "full", onStatusUpdate);
        }

        /// <summary>
        /// Queues geometry-only extraction for the given PDF ids (Enhance flow).
        /// </summary>
        public Task RunEnhanceAsync(
            IList<string> pdfIds,
            Excel.Workbook workbook,
            Action<string, string, string> onStatusUpdate)
        {
            return RunJobsAsync(pdfIds, workbook, "geometry-only", onStatusUpdate);
        }

        private async Task RunJobsAsync(
            IList<string> pdfIds,
            Excel.Workbook workbook,
            string mode,
            Action<string, string, string> onStatusUpdate)
        {
            if (pdfIds == null || pdfIds.Count == 0) return;
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));

            string workerExe = GetWorkerExePath();
            if (!File.Exists(workerExe))
            {
                string msg = $"OCR worker not found at:\n{workerExe}\n\nRun src/python/build-worker.ps1 to build it.";
                foreach (string id in pdfIds)
                    onStatusUpdate(id, "error", msg);
                return;
            }

            var jobs = LoadJobData(pdfIds, workbook, mode);

            foreach (var job in jobs)
                onStatusUpdate(job.PdfId, "queued", null);

            await Task.Run(() => RunWorker(workerExe, jobs, workbook, mode, onStatusUpdate));
        }

        private void RunWorker(
            string workerExe,
            IList<OcrJobEntry> jobs,
            Excel.Workbook workbook,
            string mode,
            Action<string, string, string> onStatusUpdate)
        {
            var psi = new ProcessStartInfo
            {
                FileName = workerExe,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.Start();

                // StandardInputEncoding / StandardOutputEncoding don't exist on
                // .NET Framework — wrap the base streams in UTF-8 readers/writers instead.
                // Do NOT dispose the originals: they own the underlying stream lifetime;
                // disposing them here would close the BaseStream that our wrappers share.
                var stdin = new StreamWriter(
                    process.StandardInput.BaseStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };

                var stdout = new StreamReader(
                    process.StandardOutput.BaseStream,
                    Encoding.UTF8);

                foreach (var job in jobs)
                {
                    Invoke(() => onStatusUpdate(job.PdfId, "processing", null));

                    // Write the job to stdin
                    string jobJson = BuildJobJson(job);
                    stdin.WriteLine(jobJson);

                    // Read lines until we get a terminal result for this job_id
                    string resultLine = ReadResultLine(stdout, job.PdfId);

                    if (resultLine == null)
                    {
                        Invoke(() => onStatusUpdate(job.PdfId, "error", "Worker closed unexpectedly."));
                        break;
                    }

                    var parsed = ParseResultLine(resultLine);
                    string status = parsed.Status;
                    string errorMessage = parsed.Error;

                    if (status == "success")
                    {
                        Invoke(() =>
                        {
                            try
                            {
                                if (string.Equals(mode, "geometry-only", StringComparison.Ordinal))
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
                        Invoke(() => onStatusUpdate(job.PdfId, "error", errorMessage));
                    }
                }

                // Close stdin so the worker exits cleanly
                stdin.Close();
                process.WaitForExit(5000);
                if (!process.HasExited)
                    process.Kill();
            }
        }

        /// <summary>
        /// Reads stdout lines until a terminal ("success" or "error") line for the
        /// given job_id is found, silently skipping "progress" lines.
        /// Returns null if the stream ends before a terminal line is received.
        /// </summary>
        private static string ReadResultLine(StreamReader stdout, string jobId)
        {
            string line;
            while ((line = stdout.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var obj = ser.Deserialize<Dictionary<string, object>>(line);
                    if (!obj.TryGetValue("job_id", out object idObj)
                        || !string.Equals(idObj?.ToString(), jobId, StringComparison.Ordinal))
                        continue;

                    if (!obj.TryGetValue("status", out object statusObj))
                        continue;

                    string st = statusObj?.ToString();
                    if (st == "progress") continue;

                    return line;
                }
                catch
                {
                    // Malformed line — skip
                }
            }
            return null;
        }

        private static OcrWorkerResult ParseResultLine(string line)
        {
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var obj = ser.Deserialize<Dictionary<string, object>>(line);
                string status = obj.TryGetValue("status", out object st) ? st?.ToString() : "error";

                if (status == "success")
                {
                    string pdfB64 = obj.TryGetValue("pdf_base64", out object b) ? b?.ToString() ?? string.Empty : string.Empty;
                    string geometryB64 = obj.TryGetValue("geometry_base64", out object g) ? g?.ToString() ?? string.Empty : string.Empty;
                    return new OcrWorkerResult
                    {
                        Status = "success",
                        PdfBase64 = pdfB64,
                        GeometryBase64 = geometryB64,
                    };
                }

                string err = obj.TryGetValue("error", out object e) ? e?.ToString() ?? "Unknown error" : "Unknown error";
                return new OcrWorkerResult { Status = "error", Error = err };
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
            AppendJsonString(sb, job.PdfId);
            sb.Append(",\"command\":\"ocr\",\"pdf_base64\":");
            AppendJsonString(sb, job.Base64);
            sb.Append(",\"mode\":");
            AppendJsonString(sb, job.Mode ?? "full");
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            if (value != null)
            {
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"':  sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n");  break;
                        case '\r': sb.Append("\\r");  break;
                        case '\t': sb.Append("\\t");  break;
                        default:
                            if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// Reads the base64 bytes for the requested PDFs from the workbook.
        /// Must be called on the UI thread.
        /// </summary>
        private static IList<OcrJobEntry> LoadJobData(IList<string> pdfIds, Excel.Workbook workbook, string mode)
        {
            var store = new CustomXml.DocuLinkCustomXmlPartStore(workbook);
            var storage = store.Load();

            var result = new List<OcrJobEntry>();
            foreach (string id in pdfIds)
            {
                var pdf = storage.Pdfs.FirstOrDefault(
                    p => string.Equals(p.Id, id, StringComparison.Ordinal));
                if (pdf != null)
                    result.Add(new OcrJobEntry { PdfId = pdf.Id, Base64 = pdf.Base64, Mode = mode });
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

        private static string GetWorkerExePath()
        {
            string codeBase = Assembly.GetExecutingAssembly().CodeBase;
            string addinDir = Path.GetDirectoryName(new Uri(codeBase).LocalPath)
                ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(addinDir, "python", "worker", "worker.exe");
        }

        private sealed class OcrJobEntry
        {
            public string PdfId { get; set; }
            public string Base64 { get; set; }
            public string Mode { get; set; }
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
