using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace DocuLink.Addin.Modules
{
    /// <summary>
    /// Lightweight persistent logger for diagnosing runtime and endpoint-security
    /// incidents without a debugger attached. Keeps session-specific files for
    /// seven days under %LOCALAPPDATA%\DocuLink\Logs\yyyy-MM-dd so each Excel
    /// restart has an independent timeline.
    /// </summary>
    internal static class DocuLinkLog
    {
        private static readonly string _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocuLink",
            "Logs");

        private static readonly DateTime _sessionStartedAt = DateTime.Now;

        /// <summary>
        /// Included in every line and in the file name because more than one Excel can
        /// be running at once (a conversion can spawn another EXCEL.EXE).
        /// </summary>
        private static readonly int _processId = GetProcessId();

        private static readonly string _sessionDirectory = Path.Combine(
            _directory,
            _sessionStartedAt.ToString("yyyy-MM-dd"));

        private static readonly string _path = Path.Combine(
            _sessionDirectory,
            $"doculink-{_sessionStartedAt:yyyyMMdd-HHmmss-fff}-p{_processId}.log");

        private static readonly object _lock = new object();

        internal static string DirectoryPath => _directory;

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
                    Directory.CreateDirectory(_sessionDirectory);
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

        /// <summary>
        /// Starts this Excel process's durable log session and removes log files older
        /// than seven days, including files created by the previous flat layout.
        /// </summary>
        internal static void StartSession()
        {
            try
            {
                Directory.CreateDirectory(_sessionDirectory);
                DateTime cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (string path in Directory.GetFiles(
                    _directory,
                    "doculink-*.log",
                    SearchOption.AllDirectories))
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
