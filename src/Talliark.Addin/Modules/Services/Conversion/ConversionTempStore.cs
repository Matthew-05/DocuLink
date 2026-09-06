using System;
using System.Collections.Generic;
using System.IO;

namespace Talliark.Addin.Modules.Services.Conversion
{
    /// <summary>
    /// Scratch space for document conversion.
    ///
    /// Every converter needs real files on disk — Office automation and WebView2
    /// printing both refuse to work from in-memory buffers — but nothing produced
    /// here outlives the import. The store hands out paths inside a per-instance
    /// folder under %TEMP%\Talliark\convert and deletes the whole folder on dispose,
    /// so a converted PDF exists only between conversion and embedding.
    ///
    /// Disposal also sweeps sibling folders left behind by a previous session that
    /// ended in a crash.
    /// </summary>
    internal sealed class ConversionTempStore : IDisposable
    {
        private const string RootFolderName = "Talliark";
        private const string ConvertFolderName = "convert";

        private readonly string _sessionDir;
        private readonly List<string> _files = new List<string>();
        private bool _disposed;

        public ConversionTempStore()
        {
            _sessionDir = Path.Combine(RootDir, "s" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sessionDir);
        }

        private static string RootDir =>
            Path.Combine(Path.GetTempPath(), RootFolderName, ConvertFolderName);

        /// <summary>Reserves a path inside the session folder. The file is not created.</summary>
        public string ReservePath(string baseName, string extension)
        {
            ThrowIfDisposed();

            string safeBase = MakeSafeFileName(baseName);
            string candidate = Path.Combine(_sessionDir, safeBase + extension);

            int suffix = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(_sessionDir, $"{safeBase}({suffix++}){extension}");
            }

            _files.Add(candidate);
            return candidate;
        }

        /// <summary>Writes bytes to a reserved path inside the session folder and returns it.</summary>
        public string WriteSource(string baseName, string extension, byte[] bytes)
        {
            string path = ReservePath(baseName, extension);
            File.WriteAllBytes(path, bytes ?? new byte[0]);
            return path;
        }

        /// <summary>Deletes one file early, e.g. a source copy no longer needed mid-batch.</summary>
        public void DeleteNow(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                TalliarkLog.Trace($"ConversionTempStore could not delete '{path}': {ex.Message}");
            }

            _files.Remove(path);
        }

        /// <summary>
        /// Removes convert folders left by earlier sessions. Called on dispose so a
        /// crashed session's scratch files do not accumulate indefinitely.
        /// </summary>
        private void SweepOrphans()
        {
            try
            {
                if (!Directory.Exists(RootDir))
                    return;

                foreach (string dir in Directory.GetDirectories(RootDir))
                {
                    if (string.Equals(dir, _sessionDir, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        // Only touch folders that have gone cold, so a concurrent
                        // Excel instance mid-import is left alone.
                        if (Directory.GetLastWriteTimeUtc(dir) > DateTime.UtcNow.AddHours(-6))
                            continue;

                        Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                        // In use or locked — leave it for a later sweep.
                    }
                }
            }
            catch (Exception ex)
            {
                TalliarkLog.Trace($"ConversionTempStore sweep failed: {ex.Message}");
            }
        }

        private static string MakeSafeFileName(string name)
        {
            string trimmed = string.IsNullOrWhiteSpace(name) ? "document" : name.Trim();
            trimmed = Path.GetFileNameWithoutExtension(trimmed);
            if (string.IsNullOrWhiteSpace(trimmed))
                trimmed = "document";

            foreach (char invalid in Path.GetInvalidFileNameChars())
                trimmed = trimmed.Replace(invalid, '_');

            // Leave headroom for the uniqueness suffix and extension within MAX_PATH.
            return trimmed.Length > 80 ? trimmed.Substring(0, 80) : trimmed;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ConversionTempStore));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (Directory.Exists(_sessionDir))
                    Directory.Delete(_sessionDir, recursive: true);
            }
            catch (Exception ex)
            {
                TalliarkLog.Trace($"ConversionTempStore could not delete '{_sessionDir}': {ex.Message}");
            }

            _files.Clear();
            SweepOrphans();
        }
    }
}
