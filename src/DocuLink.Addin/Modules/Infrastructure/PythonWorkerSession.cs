using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace DocuLink.Addin.Modules.Infrastructure
{
    /// <summary>
    /// Owns one bundled Python worker process and the newline-delimited JSON
    /// conversation with it (contracts/python-worker-v1.json).
    ///
    /// Shared by every caller that needs the worker — OCR, geometry extraction
    /// and document conversion all speak the same protocol over the same pipe,
    /// so the process/stream plumbing lives here rather than in each service.
    ///
    /// Threading contract:
    ///   • <see cref="Start"/>, <see cref="SendJob"/> and <see cref="ReadResultLine"/>
    ///     are called from a single background thread.
    ///   • <see cref="Kill"/> is safe to call from any thread.
    /// </summary>
    internal sealed class PythonWorkerSession : IDisposable
    {
        private Process _process;
        private StreamWriter _stdin;
        private StreamReader _stdout;
        private bool _disposed;

        /// <summary>True once the worker process has stopped responding or exited.</summary>
        public bool IsDead { get; private set; }

        /// <summary>Full path to the bundled CPython host executable.</summary>
        public static string WorkerExePath =>
            Path.Combine(GetWorkerDir(), "python.exe");

        /// <summary>Full path to the worker entry script.</summary>
        public static string WorkerScriptPath =>
            Path.Combine(GetWorkerDir(), "worker.py");

        /// <summary>False when the worker has not been built into the add-in output.</summary>
        public static bool IsAvailable => HasExpandedWorker(GetWorkerDir());

        public const string NotBuiltMessage =
            "Python OCR runtime is missing or incomplete. Reinstall DocuLink, or run " +
            "src/python/build-worker.ps1 for a development build.";

        /// <summary>Starts the worker process and opens UTF-8 wrappers over its pipes.</summary>
        public void Start()
        {
            if (_process != null)
                throw new InvalidOperationException("Worker session already started.");
            if (!IsAvailable)
                throw new FileNotFoundException(NotBuiltMessage, WorkerExePath);

            var psi = new ProcessStartInfo
            {
                FileName = WorkerExePath,
                Arguments = $"-I -B \"{WorkerScriptPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            _process = new Process { StartInfo = psi };
            _process.Start();

            // StandardInputEncoding / StandardOutputEncoding don't exist on
            // .NET Framework — wrap the base streams in UTF-8 readers/writers instead.
            // Do NOT dispose the originals: they own the underlying stream lifetime;
            // disposing them here would close the BaseStream our wrappers share.
            _stdin = new StreamWriter(
                _process.StandardInput.BaseStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };

            _stdout = new StreamReader(_process.StandardOutput.BaseStream, Encoding.UTF8);
        }

        /// <summary>
        /// Writes one job line to the worker's stdin. Large OCR payloads can report
        /// completed chunks so the UI remains determinate while the pipe is filled.
        /// </summary>
        public void SendJob(string jobJson, Action<int, int> onProgress = null)
        {
            if (_stdin == null)
                throw new InvalidOperationException("Worker session not started.");

            if (onProgress == null || string.IsNullOrEmpty(jobJson))
            {
                _stdin.WriteLine(jobJson);
                return;
            }

            const int chunkSize = 1024 * 1024;
            int totalChunks = (jobJson.Length + chunkSize - 1) / chunkSize;
            var buffer = new char[Math.Min(chunkSize, jobJson.Length)];
            for (int offset = 0, chunk = 1; offset < jobJson.Length; chunk++)
            {
                int count = Math.Min(buffer.Length, jobJson.Length - offset);
                jobJson.CopyTo(offset, buffer, 0, count);
                _stdin.Write(buffer, 0, count);
                _stdin.Flush();
                offset += count;
                onProgress(chunk, totalChunks);
            }
            _stdin.WriteLine();
        }

        /// <summary>
        /// Reads stdout lines until a terminal ("success" or "error") line for the
        /// given job id is found, silently skipping "progress" lines. Progress lines
        /// for the job are handed to <paramref name="onProgress"/> when supplied.
        /// Returns null — and marks the session dead — if the stream ends first.
        /// </summary>
        public string ReadResultLine(string jobId, Action<string> onProgress = null)
        {
            if (_stdout == null)
                throw new InvalidOperationException("Worker session not started.");

            string line;
            while ((line = _stdout.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                Dictionary<string, object> obj;
                try
                {
                    obj = Deserialize(line);
                }
                catch
                {
                    continue; // Malformed line — skip
                }

                if (!obj.TryGetValue("job_id", out object idObj)
                    || !string.Equals(idObj?.ToString(), jobId, StringComparison.Ordinal))
                    continue;

                if (!obj.TryGetValue("status", out object statusObj))
                    continue;

                if (string.Equals(statusObj?.ToString(), "progress", StringComparison.Ordinal))
                {
                    if (onProgress != null && obj.TryGetValue("message", out object msgObj))
                        onProgress(msgObj?.ToString() ?? string.Empty);
                    continue;
                }

                return line;
            }

            IsDead = true;
            return null;
        }

        /// <summary>Terminates the worker immediately. Safe to call from any thread.</summary>
        public void Kill()
        {
            IsDead = true;
            try { _process?.Kill(); } catch { }
        }

        /// <summary>Parses a worker JSON line into a loosely-typed dictionary.</summary>
        public static Dictionary<string, object> Deserialize(string line)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return serializer.Deserialize<Dictionary<string, object>>(line);
        }

        /// <summary>Reads a string field from a parsed worker message, defaulting to empty.</summary>
        public static string GetString(Dictionary<string, object> obj, string key)
        {
            return obj != null && obj.TryGetValue(key, out object value)
                ? value?.ToString() ?? string.Empty
                : string.Empty;
        }

        /// <summary>
        /// Reads a nested object field from a parsed worker message, returning null when
        /// the key is absent or does not hold an object. Callers must tolerate null so an
        /// older worker build that omits optional objects cannot break the host.
        /// </summary>
        public static Dictionary<string, object> GetDictionary(
            Dictionary<string, object> obj, string key)
        {
            return obj != null && obj.TryGetValue(key, out object value)
                ? value as Dictionary<string, object>
                : null;
        }

        /// <summary>Appends a JSON-escaped string literal (including quotes) to the builder.</summary>
        public static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            if (value != null)
            {
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Closing stdin lets the worker exit cleanly; it may throw if the
            // process was already killed.
            try { _stdin?.Close(); } catch { }

            try
            {
                if (_process != null)
                {
                    _process.WaitForExit(5000);
                    if (!_process.HasExited)
                        _process.Kill();
                }
            }
            catch { }

            try { _process?.Dispose(); } catch { }
            _process = null;
            _stdin = null;
            _stdout = null;
        }

        private static string GetAddinDir()
        {
            string codeBase = Assembly.GetExecutingAssembly().CodeBase;
            return Path.GetDirectoryName(new Uri(codeBase).LocalPath)
                ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        private static string GetWorkerDir()
        {
            return Path.Combine(GetAddinDir(), "python", "worker");
        }

        private static bool HasExpandedWorker(string directory)
        {
            return File.Exists(Path.Combine(directory, "python.exe"))
                && File.Exists(Path.Combine(directory, "worker.py"));
        }

    }
}
