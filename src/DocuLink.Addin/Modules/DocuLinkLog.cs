using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace DocuLink.Addin.Modules
{
    /// <summary>
    /// Lightweight file logger for diagnosing runtime issues without a debugger attached.
    /// Writes timestamped lines to %TEMP%\doculink-debug.log.
    /// Remove call sites once the issue is resolved.
    /// </summary>
    internal static class DocuLinkLog
    {
        private static readonly string _path = Path.Combine(
            Path.GetTempPath(), "doculink-debug.log");

        private static readonly object _lock = new object();

        internal static void Trace(
            string message,
            [CallerMemberName] string member = "",
            [CallerLineNumber] int line = 0)
        {
            try
            {
                string entry = $"{DateTime.Now:HH:mm:ss.fff} [{member}:{line}] {message}";
                lock (_lock)
                    File.AppendAllText(_path, entry + Environment.NewLine);
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

        /// <summary>Deletes the log file so a fresh session starts clean.</summary>
        internal static void Clear()
        {
            try { File.Delete(_path); } catch { }
        }
    }
}
