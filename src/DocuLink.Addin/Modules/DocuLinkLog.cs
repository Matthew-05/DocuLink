using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace DocuLink.Addin.Modules
{
    /// <summary>
    /// Lightweight persistent logger for diagnosing runtime and endpoint-security
    /// incidents without a debugger attached. Keeps seven daily files under
    /// %LOCALAPPDATA%\DocuLink\Logs so an Excel restart does not erase evidence.
    /// </summary>
    internal static class DocuLinkLog
    {
        private static readonly string _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocuLink",
            "Logs");

        private static readonly string _path = Path.Combine(
            _directory,
            $"doculink-{DateTime.Now:yyyyMMdd}.log");

        private static readonly object _lock = new object();

        /// <summary>
        /// Included in every line because more than one Excel can be writing here: a
        /// conversion spawns a second EXCEL.EXE, and without the pid its lines are
        /// indistinguishable from the host's — which reads as an impossible timeline.
        /// </summary>
        private static readonly int _processId = GetProcessId();

        private static int GetProcessId()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        internal static void Trace(
            string message,
            [CallerMemberName] string member = "",
            [CallerLineNumber] int line = 0)
        {
            try
            {
                string entry = $"{DateTime.Now:HH:mm:ss.fff} [{_processId}] [{member}:{line}] {message}";
                lock (_lock)
                {
                    Directory.CreateDirectory(_directory);
                    File.AppendAllText(_path, entry + Environment.NewLine);
                }
            }
            catch { }
        }

        internal static TimingScope Time(string label) => new TimingScope(label);

        internal sealed class TimingScope : IDisposable
        {
            private readonly string _label;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

            internal TimingScope(string label)
            {
                _label = label ?? string.Empty;
            }

            public void Dispose()
            {
                Trace($"{_label} took {_stopwatch.ElapsedMilliseconds}ms");
            }
        }

        /// <summary>Starts a durable session and removes logs older than seven days.</summary>
        internal static void StartSession()
        {
            try
            {
                Directory.CreateDirectory(_directory);
                DateTime cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (string path in Directory.GetFiles(_directory, "doculink-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff)
                            File.Delete(path);
                    }
                    catch { }
                }

                Trace($"session start log={_path}");
            }
            catch { }
        }
    }
}
