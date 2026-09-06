using System;
using System.Collections.Generic;
using System.Text;
using DocuLink.Addin.Modules.Infrastructure;

namespace DocuLink.Addin.Modules.Services.Conversion
{
    /// <summary>What the Python worker produced for a convert job.</summary>
    internal sealed class PythonConversionResult
    {
        /// <summary>"pdf" when <see cref="PdfBytes"/> is populated, "html" when <see cref="Html"/> is.</summary>
        public string Kind { get; set; }

        public byte[] PdfBytes { get; set; }

        public string Html { get; set; }
    }

    /// <summary>
    /// Converts source documents through the bundled Python worker ("convert"
    /// command in contracts/python-worker-v1.json).
    ///
    /// One instance spans an import batch: the worker process is started on first
    /// use and reused for every file, which matters because process start-up
    /// dominates the cost of converting a small image.
    ///
    /// Calls block on worker I/O, so run this off the UI thread.
    /// </summary>
    internal sealed class PythonConversionConverter : IDisposable
    {
        private PythonWorkerSession _session;
        private int _jobCounter;
        private bool _disposed;

        /// <summary>False when the worker has not been built into the add-in output.</summary>
        public static bool IsAvailable => PythonWorkerSession.IsAvailable;

        public static string UnavailableMessage => PythonWorkerSession.NotBuiltMessage;

        /// <summary>
        /// Converts one source document. Returns PDF bytes, or HTML when only the
        /// host can render the source (currently .eml / .mht).
        ///
        /// Progress arrives as the worker states it — stage and counts, not a
        /// sentence to be parsed — matching the OCR path. The worker reports the
        /// "convert" stage throughout a job of this kind.
        /// </summary>
        public PythonConversionResult Convert(
            byte[] sourceBytes,
            string sourceExtension,
            string sourceName,
            Action<WorkerProgress> onProgress = null)
        {
            ThrowIfDisposed();

            if (sourceBytes == null || sourceBytes.Length == 0)
                throw new InvalidOperationException("Source document is empty.");

            EnsureSession();

            string jobId = "convert-" + (++_jobCounter).ToString();
            _session.SendJob(BuildJobJson(jobId, sourceBytes, sourceExtension, sourceName));

            string resultLine = _session.ReadResultLine(jobId, onProgress);
            if (resultLine == null)
                throw new InvalidOperationException("The conversion worker closed unexpectedly.");

            return ParseResult(resultLine);
        }

        private void EnsureSession()
        {
            if (_session != null && !_session.IsDead)
                return;

            if (_session != null)
            {
                // Previous process died mid-batch — start a fresh one so the
                // remaining files still get a chance to convert.
                _session.Dispose();
                _session = null;
            }

            var session = new PythonWorkerSession();
            session.Start();
            _session = session;
        }

        private static PythonConversionResult ParseResult(string resultLine)
        {
            Dictionary<string, object> obj;
            try
            {
                obj = PythonWorkerSession.Deserialize(resultLine);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to parse the conversion worker response: " + ex.Message, ex);
            }

            string status = PythonWorkerSession.GetString(obj, "status");
            if (!string.Equals(status, "success", StringComparison.Ordinal))
            {
                string error = PythonWorkerSession.GetString(obj, "error");
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(error) ? "Conversion failed." : error);
            }

            string kind = PythonWorkerSession.GetString(obj, "output_kind");

            if (string.Equals(kind, "html", StringComparison.Ordinal))
            {
                return new PythonConversionResult
                {
                    Kind = "html",
                    Html = PythonWorkerSession.GetString(obj, "html"),
                };
            }

            string base64 = PythonWorkerSession.GetString(obj, "pdf_base64");
            if (string.IsNullOrEmpty(base64))
                throw new InvalidOperationException("The conversion worker returned an empty PDF.");

            return new PythonConversionResult
            {
                Kind = "pdf",
                PdfBytes = System.Convert.FromBase64String(base64),
            };
        }

        private static string BuildJobJson(
            string jobId,
            byte[] sourceBytes,
            string sourceExtension,
            string sourceName)
        {
            var sb = new StringBuilder();
            sb.Append("{\"job_id\":");
            PythonWorkerSession.AppendJsonString(sb, jobId);
            sb.Append(",\"command\":\"convert\",\"source_base64\":");
            PythonWorkerSession.AppendJsonString(sb, System.Convert.ToBase64String(sourceBytes));
            sb.Append(",\"source_extension\":");
            PythonWorkerSession.AppendJsonString(sb, sourceExtension ?? string.Empty);
            sb.Append(",\"source_name\":");
            PythonWorkerSession.AppendJsonString(sb, sourceName ?? string.Empty);
            sb.Append('}');
            return sb.ToString();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PythonConversionConverter));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _session?.Dispose();
            _session = null;
        }
    }
}
